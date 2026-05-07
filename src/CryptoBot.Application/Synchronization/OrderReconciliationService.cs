using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// S66-B：交易所狀態對帳背景服務。每分鐘巡檢一次本地 Active 訂單，
/// 解決「本地寫了但交易所沒收到」「交易所收了但本地 Status 沒推進」兩類漂移。
///
/// **與 <see cref="AccountSynchronizer"/> 的分工**：
///   * AccountSynchronizer：透過 user-data WS 即時收訂單成交事件，主動推進狀態
///   * OrderReconciliationService（本服務）：兜底機制 — WS 漏接、API 逾時、process crash 留下的孤兒
///
/// **三條處置分支**（嚴格遵守時間閾值，避免誤殺正在下單的活躍請求）：
///
/// | 分支 | 條件 | 動作 |
/// |---|---|---|
/// | A1 補回 | `ExchangeOrderId == null` 且 age ≥ 1 min 且交易所**有**此 cid | 補回 ExchangeOrderId + 同步狀態 |
/// | A2 清孤兒 | `ExchangeOrderId == null` 且 age ≥ 5 min 且交易所**無**此 cid | `Reject("Orphan pending cleanup")` |
/// | B 殭屍刷新 | `ExchangeOrderId != null` 且 `Status == New` 且 age ≥ 5 min | `RefreshOrderStatusAsync` 強制同步 |
///
/// 1-5 分鐘區間且交易所暫時找不到的訂單**不動**（給 SDK 重試 + WS 補上機會），下個 tick 再判。
///
/// **IRON ⑤ 守則**：任何 reject / 狀態推進**嚴禁呼叫 `_strategy.ReportError`** 或停策略；
/// 對帳是清掃，不是失敗信號。所有變更前綴 `[RECONCILIATION]` 在 log 廣播。
/// </summary>
public sealed class OrderReconciliationService : BackgroundService
{
    /// <summary>巡檢頻率。</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>未取得 ExchangeOrderId 時，至少等多久才嘗試對帳（避開正在下單的活躍請求）。</summary>
    internal static readonly TimeSpan PendingAssignThreshold = TimeSpan.FromMinutes(1);

    /// <summary>未取得 ExchangeOrderId 且交易所查無此 cid 時，超過此時間判為孤兒並 reject。</summary>
    internal static readonly TimeSpan OrphanCleanupThreshold = TimeSpan.FromMinutes(5);

    /// <summary>已有 ExchangeOrderId 但 Status 仍 New 時，超過此時間強制 RefreshOrderStatusAsync。</summary>
    internal static readonly TimeSpan ZombieRefreshThreshold = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExchangeClient _exchange;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrderReconciliationService> _logger;

    public OrderReconciliationService(
        IServiceScopeFactory scopeFactory,
        IExchangeClient exchange,
        ILogger<OrderReconciliationService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _exchange = exchange;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[RECONCILIATION] OrderReconciliationService started — tick every {Min} min.",
            TickInterval.TotalMinutes);

        // 啟動時刻意**不**立即 tick — 系統剛啟動可能正在大量下單，等一個週期讓真實 placement 完成
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 預期的 shutdown 路徑
        }

        _logger.LogInformation("[RECONCILIATION] OrderReconciliationService stopping.");
    }

    /// <summary>
    /// 單一 tick 的對帳邏輯。<c>internal</c> 暴露給單元測試直接呼叫，避免測試需等真實計時器。
    /// </summary>
    internal async Task ReconcileOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var orderRepo = sp.GetRequiredService<IOrderRepository>();
        var uow = sp.GetRequiredService<IUnitOfWork>();

        IReadOnlyList<Order> activeOrders;
        try
        {
            activeOrders = await orderRepo.GetActiveOrdersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[RECONCILIATION] Failed to load active orders — skip this tick.");
            return;
        }

        if (activeOrders.Count == 0) return;

        var now = _clock.GetUtcNow().UtcDateTime;
        var changed = 0;

        foreach (var order in activeOrders)
        {
            if (ct.IsCancellationRequested) return;

            // S72：擴大涵蓋至 PartiallyFilled — S71 揪出 3 筆 LINK-USDT PartiallyFilled 卡 8 天，
            // 真因為 user-data WS 漏接最後 1% fill update + 原版本只處理 New。
            // 對 PartiallyFilled 只走情境 B（已有 ExchangeOrderId）路徑：
            //   age ≥ ZombieRefreshThreshold → 強制 RefreshOrderStatusAsync 讓 BingX 端最終狀態落地
            if (order.Status != OrderStatus.New && order.Status != OrderStatus.PartiallyFilled) continue;

            try
            {
                if (await ProcessOneAsync(order, now, ct).ConfigureAwait(false))
                {
                    // S66-B HOTFIX：顯式呼叫 UpdateAsync 是「安全網」。
                    // GetActiveOrdersAsync 回的 entity 預設應為 tracked，理論上 ChangeTracker
                    // 自會偵測 mutation。但首次 PM 驗收觀察到 Status 沒落地（log 顯示 A2 但 DB 留 1），
                    // 補這行可在 entity 因任何理由變 Detached 的場景強制標 Modified — 對 tracked
                    // 而言為 no-op，零副作用。配合下方 rowsAffected 觀測，若仍漂移可立刻定位。
                    await orderRepo.UpdateAsync(order, ct).ConfigureAwait(false);
                    changed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[RECONCILIATION] Failed to reconcile order {OrderId} (cid={Cid}) — skip.",
                    order.Id, order.ClientOrderId);
            }
        }

        if (changed > 0)
        {
            try
            {
                // S66-B HOTFIX：捕獲 rowsAffected 並比對 changed。若 SaveChanges 回 0 卻有 changed > 0，
                // 代表 ChangeTracker 真的沒抓到 mutation — 這是事故偵測器，必須立刻浮上水面。
                var rowsAffected = await uow.SaveChangesWithRetryAsync(ct: ct).ConfigureAwait(false);

                if (rowsAffected != changed)
                {
                    _logger.LogWarning(
                        "[RECONCILIATION] DB persistence mismatch! changed={Changed} but SaveChanges affected {Rows} rows. " +
                        "Indicates EF tracking gap — verify Order entity tracking state.",
                        changed, rowsAffected);
                }
                else
                {
                    _logger.LogInformation(
                        "[RECONCILIATION] tick complete: {Changed}/{Total} orders updated, {Rows} rows persisted.",
                        changed, activeOrders.Count, rowsAffected);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[RECONCILIATION] SaveChanges failed at tick end — {Count} mutations may be lost (will retry next tick).",
                    changed);
            }
        }
    }

    /// <summary>
    /// 處理單一 Order。回傳 true 表示有實際 mutation 需要 SaveChanges。
    /// 拋例外代表「這個 order 處理失敗、整個 tick 不該因此中斷」— 由呼叫端 catch + log。
    /// </summary>
    private async Task<bool> ProcessOneAsync(Order order, DateTime now, CancellationToken ct)
    {
        var age = now - order.CreatedAt;

        // ===== 情境 A：未取得 ExchangeOrderId =====
        if (string.IsNullOrEmpty(order.ExchangeOrderId))
        {
            // 太年輕的不動（PendingAssignThreshold 內可能正在下單）
            if (age < PendingAssignThreshold) return false;

            if (string.IsNullOrEmpty(order.ClientOrderId))
            {
                _logger.LogWarning(
                    "[RECONCILIATION] Order {OrderId} has neither ClientOrderId nor ExchangeOrderId — cannot reconcile (age={Age}).",
                    order.Id, age);
                return false;
            }

            ExchangeOrderSnapshot? remote;
            try
            {
                remote = await _exchange
                    .GetOrderByClientOrderIdAsync(order.Symbol, order.ClientOrderId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[RECONCILIATION] GetOrderByClientOrderIdAsync failed for cid={Cid} — retry next tick.",
                    order.ClientOrderId);
                return false;
            }

            // A1：交易所有此單 → 補回 ExchangeOrderId + 同步狀態
            if (remote is not null)
            {
                _logger.LogInformation(
                    "[RECONCILIATION] A1 found remote: cid={Cid} -> eid={Eid}, status={Status} (age={Age}).",
                    order.ClientOrderId, remote.ExchangeOrderId, remote.Status, age);

                order.AssignExchangeOrderId(remote.ExchangeOrderId);
                ApplyRemoteStatus(order, remote);
                return true;
            }

            // A2：交易所查無 + 超過 5 分鐘 → 孤兒清理
            if (age >= OrphanCleanupThreshold)
            {
                _logger.LogWarning(
                    "[RECONCILIATION] A2 orphan cleanup: cid={Cid} age={AgeMin:F1}min — local Reject.",
                    order.ClientOrderId, age.TotalMinutes);
                order.Reject($"Orphan pending cleanup (age={age.TotalMinutes:F1}min)");
                return true;
            }

            // 1-5 分鐘區間：可能還在 SDK 重試窗口，下個 tick 再說
            return false;
        }

        // ===== 情境 B：已有 ExchangeOrderId 但 Status 仍 New 且超過 5 分鐘 =====
        if (age >= ZombieRefreshThreshold)
        {
            _logger.LogInformation(
                "[RECONCILIATION] B zombie refresh: orderId={OrderId} eid={Eid} age={AgeMin:F1}min.",
                order.Id, order.ExchangeOrderId, age.TotalMinutes);

            try
            {
                // RefreshOrderStatusAsync 內部會 mutate order（RecordFill / Cancel / Reject / Expire）
                await _exchange.RefreshOrderStatusAsync(order, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[RECONCILIATION] RefreshOrderStatusAsync failed for eid={Eid} — retry next tick.",
                    order.ExchangeOrderId);
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// A1 補回時，依 remote 狀態推進本地 order。
    /// 不重複呼叫 RefreshOrderStatusAsync —— 我們已有 snapshot，直接套用。
    /// </summary>
    private static void ApplyRemoteStatus(Order order, ExchangeOrderSnapshot remote)
    {
        if (!order.IsActive) return;  // 防保護：理論上 caller 已過濾 Status==New，這是 belt-and-suspenders

        switch (remote.Status)
        {
            case OrderStatus.Canceled:
                order.Cancel("Reconcile: canceled on exchange");
                break;
            case OrderStatus.Rejected:
                order.Reject("Reconcile: rejected on exchange");
                break;
            case OrderStatus.Expired:
                order.Expire();
                break;
            case OrderStatus.Filled:
            case OrderStatus.PartiallyFilled:
                if (remote.QuantityFilled > order.FilledQuantity.Value
                    && remote.AveragePrice is decimal avg && avg > 0)
                {
                    var delta = remote.QuantityFilled - order.FilledQuantity.Value;
                    // commission 從 snapshot 取不到（BingX GetOrderAsync 的 fee 欄位另查），這裡記 0
                    // 實際手續費由 AccountSynchronizer 收 WS trade 事件時補上
                    order.RecordFill(
                        Quantity.Create(delta),
                        Price.Create(avg),
                        commission: 0m);
                }
                break;
            // OrderStatus.New 等同「狀態未變」，不動
        }
    }
}
