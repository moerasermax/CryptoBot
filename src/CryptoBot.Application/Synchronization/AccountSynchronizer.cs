using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Realtime;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// <see cref="IAccountSynchronizer"/> 的預設實作。
///
/// DI 生命週期：Singleton（掛 WS handler 需要持久對象）。
/// 每次收到事件內部會開一個 Scoped DI scope 拿 repository + UnitOfWork。
///
/// 設計要點：
/// - <see cref="StartAsync"/> 幂等 — 使用 bool 防重掛 handler。
/// - 所有對 Order aggregate 的狀態改動都必須配合 <see cref="IUnitOfWork.SaveChangesAsync"/>
///   寫回 DB；否則 WS 事件白處理。
/// - Position 平倉需要退出價：WS 事件本身沒提供，我們 on-demand 從 <see cref="IExchangeClient.GetMarkPriceAsync"/>
///   取當下 MarkPrice 當作近似退出價。
/// - 所有 handler 內部的例外都 log 後吞掉 — WS 執行緒一路上拋會導致 SDK 連線被拖垮。
/// </summary>
public sealed class AccountSynchronizer : IAccountSynchronizer
{
    private readonly IMarketDataStream _marketData;
    private readonly IExchangeClient _exchange;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountSynchronizer> _logger;
    private readonly IRealtimeBroadcaster? _broadcaster;

    private Func<ExchangeOrderUpdate, Task>? _orderHandler;
    private Func<ExchangeAccountUpdate, Task>? _accountHandler;
    private Func<Symbol, Price, Task>? _priceHandler;
    private bool _running;

    // FINAL-STABILITY T7：已訂閱 MarkPrice stream 的 symbol 集合。
    // 避免同一 symbol 重複訂（SDK 內部其實也擋，但這裡先防一道不浪費網路來回）。
    // 訂閱時機：Reconcile 盤點完、以及 OrderFill 把新 Position 打進 DB 後。
    private readonly HashSet<Symbol> _pnlSubscribed = new();
    private readonly object _pnlSubscribedLock = new();

    // S31：broadcaster 設成可選，現存測試 call site 不必逐一改動；
    // Web host 時 DI 會注入 SignalR 版本，UI 的 TradeHistoryTable 才能即時刷新。
    public AccountSynchronizer(
        IMarketDataStream marketData,
        IExchangeClient exchange,
        IServiceScopeFactory scopeFactory,
        ILogger<AccountSynchronizer> logger,
        IRealtimeBroadcaster? broadcaster = null)
    {
        _marketData = marketData;
        _exchange = exchange;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _broadcaster = broadcaster;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_running) return Task.CompletedTask;

        _orderHandler = HandleOrderUpdateAsync;
        _accountHandler = HandleAccountUpdateAsync;
        _priceHandler = HandlePriceUpdateAsync;
        _marketData.OnExchangeOrderUpdate += _orderHandler;
        _marketData.OnExchangeAccountUpdate += _accountHandler;
        // FINAL-STABILITY T7：接上獨立 MarkPrice WS tick stream —
        // BingX account update 只在帳戶/持倉「狀態改變」時推、且不帶 MarkPrice（寫死 0）。
        // 要「PnL 每秒跳動」就必須吃 MarkPrice tick stream，在這裡 broadcast 一次就好，
        // 不寫 DB（避免每秒 UPDATE Position 引發 concurrency 洪水）。
        _marketData.OnPriceUpdate += _priceHandler;
        _running = true;

        _logger.LogInformation(
            "AccountSynchronizer subscribed: order + account + price (MarkPrice tick stream).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_running) return Task.CompletedTask;

        if (_orderHandler is not null)
        {
            _marketData.OnExchangeOrderUpdate -= _orderHandler;
            _orderHandler = null;
        }
        if (_accountHandler is not null)
        {
            _marketData.OnExchangeAccountUpdate -= _accountHandler;
            _accountHandler = null;
        }
        if (_priceHandler is not null)
        {
            _marketData.OnPriceUpdate -= _priceHandler;
            _priceHandler = null;
        }
        lock (_pnlSubscribedLock) _pnlSubscribed.Clear();
        _running = false;

        _logger.LogInformation("AccountSynchronizer unsubscribed.");
        return Task.CompletedTask;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("AccountSynchronizer starting reconciliation…");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var orderRepo = sp.GetRequiredService<IOrderRepository>();
        var positionRepo = sp.GetRequiredService<IPositionRepository>();
        var uow = sp.GetRequiredService<IUnitOfWork>();

        // 1) 訂單對帳 — 把每筆尚未終結的本地訂單都打 REST 更新一次
        var activeOrders = await orderRepo.GetActiveOrdersAsync(ct).ConfigureAwait(false);
        var refreshed = 0;
        foreach (var order in activeOrders)
        {
            if (string.IsNullOrEmpty(order.ExchangeOrderId))
            {
                _logger.LogWarning(
                    "Reconcile skip: local order {Id} has no ExchangeOrderId (likely never reached exchange).",
                    order.Id);
                continue;
            }

            try
            {
                await _exchange.RefreshOrderStatusAsync(order, ct).ConfigureAwait(false);
                refreshed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconcile: refresh failed for order {Id} (exchangeId={ExId}).",
                    order.Id, order.ExchangeOrderId);
            }
        }

        // 2) 倉位對帳 — S72 實證對帳：本地 Open / 遠端不存在時，嚴禁用 MarkPrice 推算成交價（IM §S72 鐵則）。
        //    走 GetTradeHistoryAsync 取真實成交、找到才結算；查無實證即降級 Unaccounted 並廣播 [CRITICAL_SYNC]。
        var localOpen = await positionRepo.GetOpenPositionsAsync(ct).ConfigureAwait(false);
        var remoteOpen = await _exchange.GetOpenPositionsAsync(ct).ConfigureAwait(false);

        var closedBroadcasts = new List<PositionClosedUpdate>();
        var closedOrphans = 0;
        foreach (var local in localOpen)
        {
            var matched = remoteOpen.Any(r =>
                r.Symbol.Equals(local.Symbol) &&
                r.Side == local.Side &&
                r.Quantity > 0m);
            if (matched) continue;

            if (await TryCloseWithEvidenceAsync(local, "Reconcile", positionRepo, closedBroadcasts, ct)
                .ConfigureAwait(false))
            {
                closedOrphans++;
            }
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Reconciliation complete: refreshed {Orders} orders, closed {Orphans} orphan positions.",
            refreshed, closedOrphans);

        // SaveChanges 成功後再 fire PositionClosed 廣播 — 與 HandleAccountUpdateAsync 對齊（避免 UI 提前收到事件查無資料）。
        if (_broadcaster is not null && closedBroadcasts.Count > 0)
        {
            foreach (var payload in closedBroadcasts)
            {
                try
                {
                    await _broadcaster.BroadcastPositionClosedAsync(payload, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Reconcile: BroadcastPositionClosedAsync failed for {Id}.", payload.PositionId);
                }
            }
        }

        // FINAL-STABILITY T7：盤點完畢後，把當前仍開倉的 symbol 全部訂上 MarkPrice tick stream。
        // 開機後第一次 Reconcile 會把還沒來得及訂閱的遺留持倉接上，後續 OrderUpdate 成交才補掛的那段交由
        // HandleOrderUpdateAsync 的尾端處理。
        foreach (var symbol in localOpen.Select(p => p.Symbol).Distinct())
            await EnsureMarkPriceSubscribedAsync(symbol, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// FINAL-STABILITY T7：冪等訂閱。第一次見到 symbol 才送 WS subscribe；已訂過直接放過。
    /// 失敗只 log warning — WS 少一條訂閱不該把 Reconcile / OrderFill 主流程拖掛。
    /// </summary>
    private async Task EnsureMarkPriceSubscribedAsync(Symbol symbol, CancellationToken ct)
    {
        lock (_pnlSubscribedLock)
        {
            if (!_pnlSubscribed.Add(symbol)) return;
        }

        try
        {
            await _marketData.SubscribeMarkPriceAsync(symbol, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Subscribed MarkPrice WS stream for {Symbol} (PnL ticker).",
                symbol.BingXFormat);
        }
        catch (Exception ex)
        {
            // 訂閱失敗就把它從 set 移除，下次 OrderFill 有機會再試一次。
            lock (_pnlSubscribedLock) _pnlSubscribed.Remove(symbol);
            _logger.LogWarning(ex,
                "SubscribeMarkPriceAsync failed for {Symbol} — PnL tick stream not live for this symbol.",
                symbol.BingXFormat);
        }
    }

    // ========== WS event handlers ==========

    private async Task HandleOrderUpdateAsync(ExchangeOrderUpdate update)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var orderRepo = sp.GetRequiredService<IOrderRepository>();
            var uow = sp.GetRequiredService<IUnitOfWork>();

            var order = await orderRepo.GetByExchangeOrderIdAsync(update.ExchangeOrderId, CancellationToken.None)
                .ConfigureAwait(false);
            if (order is null)
            {
                _logger.LogDebug(
                    "Order update for unknown exchange order {ExId} ({Symbol} {Status}) — ignoring.",
                    update.ExchangeOrderId, update.Symbol, update.Status);
                return;
            }

            ApplyOrderStateTransition(order, update);
            await orderRepo.UpdateAsync(order, CancellationToken.None).ConfigureAwait(false);
            // S53 T2：WS handler 與 StrategyExecutor 下單可能同時 UPDATE 同一行 order；
            //         走重試版避免丟失遠端成交事件（client-wins 合併後客端值為最新）。
            await uow.SaveChangesWithRetryAsync(ct: CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Order {ExId} → {Status} (filled={Filled}/{Qty}, avg={Avg}).",
                update.ExchangeOrderId, update.Status,
                update.QuantityFilled, update.Quantity, update.AverageFillPrice);

            // FINAL-STABILITY T7：一旦成交訊號進來，把該 symbol 的 MarkPrice WS 流補訂起來。
            // 冪等 — 已訂過的 symbol 會被 EnsureMarkPriceSubscribedAsync 過濾掉。
            if (update.Status == OrderStatus.Filled || update.Status == OrderStatus.PartiallyFilled)
                await EnsureMarkPriceSubscribedAsync(update.Symbol, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AccountSynchronizer: error handling order update {ExId}.",
                update.ExchangeOrderId);
        }
    }

    private async Task HandleAccountUpdateAsync(ExchangeAccountUpdate update)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var positionRepo = sp.GetRequiredService<IPositionRepository>();
            var uow = sp.GetRequiredService<IUnitOfWork>();

            // S31：本批被平倉的 broadcast payload — 等 SaveChangesAsync 提交後再統一 fire，
            // 確保 UI 收到事件去 re-fetch /api/dashboard/trade-history 時資料已入庫。
            var closedBroadcasts = new List<PositionClosedUpdate>();

            foreach (var remote in update.Positions)
            {
                var openLocal = await positionRepo
                    .GetOpenPositionsBySymbolAsync(remote.Symbol, CancellationToken.None)
                    .ConfigureAwait(false);
                var match = openLocal.FirstOrDefault(p => p.Side == remote.Side);
                if (match is null) continue;

                if (remote.Quantity == 0m)
                {
                    // S72：實證對帳 — 嚴禁用 remote.MarkPrice / GetMarkPriceAsync 推算 ExitPrice（IM §S72 鐵則）。
                    // 走 GetTradeHistoryAsync 取真實成交；查無實證即降級 Unaccounted + 廣播 [CRITICAL_SYNC]。
                    await TryCloseWithEvidenceAsync(
                        match, "AccountUpdateWS", positionRepo, closedBroadcasts, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else if (remote.MarkPrice > 0m)
                {
                    match.UpdateCurrentPrice(Price.Create(remote.MarkPrice));
                    await positionRepo.UpdateAsync(match, CancellationToken.None).ConfigureAwait(false);

                    // S42 T3：把最新的 MarkPrice + uPnL 推給 Dashboard 表格，
                    // 不等下一筆交易才刷整包，單筆 broadcast 失敗只吞掉 log（WS handler 不能上拋）。
                    if (_broadcaster is not null)
                    {
                        try
                        {
                            await _broadcaster.BroadcastPositionPnLAsync(new PositionPnLTickUpdate(
                                PositionId: match.Id,
                                Symbol: match.Symbol.BingXFormat,
                                CurrentPrice: remote.MarkPrice,
                                UnrealizedPnL: match.UnrealizedPnL,
                                UnrealizedPnLPercent: match.UnrealizedPnLPercent),
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "BroadcastPositionPnLAsync failed for {Id} — UI PnL may tick late.",
                                match.Id);
                        }
                    }
                }
            }

            // S53 T2：批次 Position 更新可能與 StrategyExecutor.HandleSignalAsync 同時寫入；走重試版。
            await uow.SaveChangesWithRetryAsync(ct: CancellationToken.None).ConfigureAwait(false);

            // SaveChanges 成功後再廣播，避免 UI 收到事件後查不到資料。
            // 單筆 broadcast 失敗不阻擋其他筆 — WS handler thread 絕不能上拋。
            if (_broadcaster is not null && closedBroadcasts.Count > 0)
            {
                foreach (var payload in closedBroadcasts)
                {
                    try
                    {
                        await _broadcaster.BroadcastPositionClosedAsync(
                            payload, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "BroadcastPositionClosedAsync failed for {Id} — UI history may miss this close.",
                            payload.PositionId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AccountSynchronizer: error handling account update.");
        }
    }

    /// <summary>
    /// FINAL-STABILITY T7：MarkPrice tick handler —「PnL 每秒跳動」的真正源頭。
    ///
    /// <para>
    /// BingX 的 account update WS 只在帳戶狀態變化時推、且 payload 的 MarkPrice 寫死 0，
    /// 所以 <see cref="HandleAccountUpdateAsync"/> 裡的 PnL 廣播路徑實務上很少命中。
    /// 我們改掛獨立的 MarkPrice tick stream：每拍都進來這裡，用「當前價 + entry + side + leverage」
    /// 直接算 unrealized PnL 並 broadcast，不走 DB、也不走 <c>Position.UpdateCurrentPrice()</c>
    /// — 後者會觸發 TrailingStop 更新 + SL/TP 檢查（有 Domain event），tick handler 不該吃那副作用。
    /// </para>
    ///
    /// <para>
    /// 無 broadcaster（test / headless 模式）直接 early-return，完全不碰 DB 避免空轉。
    /// </para>
    /// </summary>
    private async Task HandlePriceUpdateAsync(Symbol symbol, Price price)
    {
        if (_broadcaster is null) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var positionRepo = sp.GetRequiredService<IPositionRepository>();

            var openAtSymbol = await positionRepo
                .GetOpenPositionsBySymbolAsync(symbol, CancellationToken.None)
                .ConfigureAwait(false);
            if (openAtSymbol.Count == 0) return;

            var symbolStr = symbol.BingXFormat;
            var mark = price.Value;

            foreach (var p in openAtSymbol)
            {
                // inline 計算 — 不修改 Entity，避免 EF tracker 把這些 tick 當 dirty 寫回 DB。
                var diff = mark - p.EntryPrice.Value;
                var pnl = p.Side == Domain.Enums.PositionSide.Long
                    ? diff * p.Quantity.Value
                    : -diff * p.Quantity.Value;
                var priceChangePct = p.EntryPrice.Value == 0m ? 0m
                    : (mark - p.EntryPrice.Value) / p.EntryPrice.Value;
                var signedPct = p.Side == Domain.Enums.PositionSide.Long
                    ? priceChangePct : -priceChangePct;
                var pnlPct = signedPct * p.Leverage.Value * 100m;

                try
                {
                    await _broadcaster.BroadcastPositionPnLAsync(new PositionPnLTickUpdate(
                        PositionId: p.Id,
                        Symbol: symbolStr,
                        CurrentPrice: mark,
                        UnrealizedPnL: pnl,
                        UnrealizedPnLPercent: pnlPct),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "BroadcastPositionPnLAsync failed for {Id} ({Symbol}) — UI PnL may tick late.",
                        p.Id, symbolStr);
                }
            }
        }
        catch (Exception ex)
        {
            // WS handler 絕不能上拋。MarkPrice 量大，只 log warning 不 Error。
            _logger.LogWarning(ex,
                "HandlePriceUpdateAsync: unexpected error for {Symbol} @ {Price} — tick dropped.",
                symbol.BingXFormat, price.Value);
        }
    }

    /// <summary>
    /// S72：實證對帳共用路徑 — 對「本地 Open / 遠端不存在」的 Position 做平倉決策。
    ///
    /// <para>
    /// 嚴禁用 <see cref="IExchangeClient.GetMarkPriceAsync"/> 推算 ExitPrice（IM §S72 鐵則）。
    /// 改為呼叫 <see cref="IExchangeClient.GetTradeHistoryAsync"/> 取真實成交：
    /// </para>
    /// <list type="number">
    ///   <item>有對應 closing trades → 加權平均成交價結算 → <see cref="Position.Close"/></item>
    ///   <item>無對應 closing trades（Unaccounted）→ 用 EntryPrice 平倉（RealizedPnL≈0、僅反映既有 commission）+ LogError + 廣播 [CRITICAL_SYNC]</item>
    ///   <item>GetTradeHistoryAsync 本身失敗 → 保留 Open + 廣播 [CRITICAL_SYNC] ReconcileFailed，等下次重試</item>
    /// </list>
    /// 回傳 <c>true</c> 代表 Position 已被平倉（有實際 mutation 需 SaveChanges 入庫）；
    /// 回傳 <c>false</c> 代表保留 Open（GetTradeHistoryAsync 失敗）。
    /// </summary>
    private async Task<bool> TryCloseWithEvidenceAsync(
        Position local,
        string sourceTag,
        IPositionRepository positionRepo,
        List<PositionClosedUpdate> closedBroadcasts,
        CancellationToken ct)
    {
        IReadOnlyList<ExchangeTradeInfo> trades;
        try
        {
            trades = await _exchange.GetTradeHistoryAsync(local.Symbol, local.OpenedAt, until: null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CRITICAL_SYNC] {Source}: GetTradeHistoryAsync failed for {Symbol} (Position={Id}) — " +
                "keeping Open, will retry next tick. Manual investigation may be required if persistent.",
                sourceTag, local.Symbol, local.Id);
            await BroadcastReconciliationCriticalAsync(
                "ReconcileFailed", local,
                $"[{sourceTag}] GetTradeHistoryAsync failed: {ex.Message}", ct)
                .ConfigureAwait(false);
            return false;
        }

        // 對應此 Position 的「平倉成交」：(a) Side 反向（Long Position 平倉 = Sell）（b) PositionSide 同
        var closingSide = local.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        var closingTrades = trades
            .Where(t => t.Side == closingSide && t.PositionSide == local.Side)
            .ToList();

        if (closingTrades.Count == 0)
        {
            // S77 fix: 對齊 IM §S72「『我看不到』≠『已平倉』」鐵則 + 「狀態變更必須以交易所 REST 實證為據」+
            //         「查無實證時保留本地 Open + 報錯，不結算」(IM §S72 §B / §S72 預防鐵則 #1+#5)。
            //
            // 既有實作 `local.Close(local.EntryPrice, ...)` 是 phantom close 真實根因：
            //   - BingX GetOpenPositions 短暫缺項（WS/REST 一致性偏差、API 漏接）
            //     ≠ 部位真實已平倉
            //   - GetTradeHistoryAsync 查無 closing trade ≠ 部位真實已平倉
            //   - 用 EntryPrice 強制 close 違反 IM §S72 既有警示，
            //     且配合 Position.Close PnL bug (commit S77) 會產生假宣告獲利 +Commission
            //
            // 修法：保留 Open + 廣播 critical alert + return false（不算成功處理）。
            // 下次 reconcile tick 會 retry — BingX history 補上後就能走正常 evidenced close path。
            // 若 user 確認 BingX 真實已平倉但 history 永遠缺項，由 user 手動 SQL 處理（IM §S72 §A 模式）。
            _logger.LogError(
                "[CRITICAL_SYNC] {Source} Unaccounted: Position {Id} ({Symbol} {Side} qty={Qty} entry={Entry}) " +
                "appears closed remotely (GetOpenPositions 缺項) but NO closing trade found in BingX history since {Since:u}. " +
                "Keeping Open per IM §S72 — will retry next tick. " +
                "If persistent, manual investigation required (BingX history 漏接 or user 端 GUI 平倉未經 strategy).",
                sourceTag, local.Id, local.Symbol, local.Side, local.Quantity.Value,
                local.EntryPrice.Value, local.OpenedAt);

            await BroadcastReconciliationCriticalAsync(
                "PositionUnaccounted", local,
                $"[{sourceTag}] No closing trade in BingX history since {local.OpenedAt:u}. " +
                $"Keeping Open per IM §S72 (was previously phantom-closed with EntryPrice, fixed in S77).",
                ct)
                .ConfigureAwait(false);
            return false;  // 不算成功處理，下次 tick 再 retry
        }

        // 有實證：以加權平均成交價結算
        var totalQty = closingTrades.Sum(t => t.Quantity);
        var weightedExit = totalQty > 0m
            ? closingTrades.Sum(t => t.Price * t.Quantity) / totalQty
            : closingTrades[0].Price;
        var exitPrice = Price.Create(weightedExit);

        _logger.LogInformation(
            "[RECONCILIATION] {Source} evidenced close: Position {Id} ({Symbol} {Side}) settled with " +
            "{N} closing trades, weighted exit price={Exit}, total filled qty={Qty}.",
            sourceTag, local.Id, local.Symbol, local.Side,
            closingTrades.Count, weightedExit, totalQty);

        local.Close(exitPrice, reason: $"{sourceTag} evidenced from {closingTrades.Count} trades");
        await positionRepo.UpdateAsync(local, ct).ConfigureAwait(false);

        if (_broadcaster is not null)
        {
            closedBroadcasts.Add(BuildPositionClosedPayload(local, exitPrice));
        }

        return true;
    }

    /// <summary>
    /// S72：[CRITICAL_SYNC] 廣播包裝 — broadcaster 為 null（test / headless）時 no-op；
    /// 廣播本身失敗只 log warning，不阻擋對帳主流程（與 PositionClosed 廣播策略一致）。
    /// </summary>
    private async Task BroadcastReconciliationCriticalAsync(
        string category, Position local, string detail, CancellationToken ct)
    {
        if (_broadcaster is null) return;
        try
        {
            await _broadcaster.BroadcastReconciliationCriticalAsync(
                new ReconciliationCriticalUpdate(
                    OccurredAtUtc: DateTime.UtcNow,
                    Category: category,
                    Symbol: local.Symbol.BingXFormat,
                    EntityId: local.Id.ToString(),
                    Detail: detail),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BroadcastReconciliationCriticalAsync failed for {Id} — UI may miss this critical sync event.",
                local.Id);
        }
    }

    private static PositionClosedUpdate BuildPositionClosedPayload(Position local, Price exitPrice)
        => new PositionClosedUpdate(
            PositionId: local.Id,
            ClosedAtUtc: local.ClosedAt ?? DateTime.UtcNow,
            Symbol: local.Symbol.BingXFormat,
            PositionSide: local.Side.ToString(),
            ExitPrice: exitPrice.Value,
            RealizedPnL: local.RealizedPnL);

    private static void ApplyOrderStateTransition(Order order, ExchangeOrderUpdate update)
    {
        // 成交進度：如果遠端已成交量 > 本地，補齊差額
        var localFilled = order.FilledQuantity.Value;
        if (update.QuantityFilled > localFilled &&
            update.AverageFillPrice is > 0m)
        {
            var delta = update.QuantityFilled - localFilled;
            order.RecordFill(
                filledQty: Quantity.Create(delta),
                fillPrice: Price.Create(update.AverageFillPrice.Value),
                commission: update.Fee);
        }

        if (!order.IsActive) return;

        switch (update.Status)
        {
            case OrderStatus.Canceled: order.Cancel("Canceled on exchange"); break;
            case OrderStatus.Rejected: order.Reject("Rejected on exchange"); break;
            case OrderStatus.Expired:  order.Expire(); break;
        }
    }
}
