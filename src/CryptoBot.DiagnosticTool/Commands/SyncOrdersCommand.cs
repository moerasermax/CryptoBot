using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S61 Ghost Order Inspector — 本地 <c>Orders</c> 表 (Active) 與交易所活躍掛單雙向對帳。
///
/// 三類輸出：
///   - <b>Local-only</b>：本地仍是 Active、雲端已無 — 可能為「已失效但本地未更新」的殭屍單。
///   - <b>Exchange-only</b>：雲端有、本地完全無記錄 — <b>幽靈訂單</b>（極度危險，極可能是第三方來源或
///     本機 DB 毀損）。顯著警告、提供建議取消動作。
///   - <b>Status / Quantity mismatch</b>：雙方都有、但狀態或成交量不同 — 本地落後於雲端的常見情形。
///
/// 指令**純讀**，不自動修復（避免工具誤操作下單 / 撤單）。建議修復動作印在輸出末尾。
/// </summary>
public sealed class SyncOrdersCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SyncOrdersCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "s61_sync-orders";
    public IReadOnlyList<string> Aliases => new[] { "sync-orders", "ghost-orders" };
    public string Description => "Compare local Active orders with exchange open orders; flag ghosts.";
    public string Usage => "s61_sync-orders <Symbol>";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            return 2;
        }

        Symbol symbol;
        try { symbol = Symbol.Parse(args[0]); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid symbol '{args[0]}': {ex.Message}");
            return 2;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var orderRepo = sp.GetRequiredService<IOrderRepository>();
        var exchange = sp.GetRequiredService<IExchangeClient>();

        Console.WriteLine("=== Ghost Order Inspector ===");
        Console.WriteLine($"Symbol : {symbol.BingXFormat}");
        Console.WriteLine($"Mode   : {exchange.CurrentMode}");
        Console.WriteLine();

        // 1) 本地 Active — 過濾同 Symbol
        IReadOnlyList<Order> activeAll;
        try
        {
            activeAll = await orderRepo.GetActiveOrdersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] Local GetActiveOrdersAsync failed: {ex.Message}");
            return 4;
        }

        var localActive = activeAll.Where(o => o.Symbol.Equals(symbol)).ToList();

        // 2) 本地 Active 中可能有尚未 assign ExchangeOrderId 的新單（剛 placed 還沒拿到 id）
        //    這類跳過雙向比對，單獨列為「pending-assign」讓 PM 知道存在。
        var pendingAssign = localActive.Where(o => string.IsNullOrEmpty(o.ExchangeOrderId)).ToList();
        var localWithId = localActive
            .Where(o => !string.IsNullOrEmpty(o.ExchangeOrderId))
            .ToDictionary(o => o.ExchangeOrderId!, StringComparer.Ordinal);

        // 3) 交易所活躍掛單
        IReadOnlyList<ExchangeOpenOrderInfo> exchangeOrders;
        try
        {
            exchangeOrders = await exchange.GetOpenOrdersAsync(symbol, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] Exchange GetOpenOrdersAsync failed: {ex.Message}");
            return 5;
        }

        var exchangeById = exchangeOrders.ToDictionary(o => o.ExchangeOrderId, StringComparer.Ordinal);

        Console.WriteLine($"Local Active ({symbol.BingXFormat}) : {localActive.Count} " +
                          $"(with exchange id: {localWithId.Count}, pending assign: {pendingAssign.Count})");
        Console.WriteLine($"Exchange Open orders        : {exchangeOrders.Count}");
        Console.WriteLine();

        // 4) 對帳
        var localOnly = new List<Order>();
        var exchangeOnly = new List<ExchangeOpenOrderInfo>();
        var mismatches = new List<(Order Local, ExchangeOpenOrderInfo Remote, string Reason)>();
        var aligned = 0;

        foreach (var (id, local) in localWithId)
        {
            if (!exchangeById.TryGetValue(id, out var remote))
            {
                localOnly.Add(local);
                continue;
            }

            var reason = DiagnoseMismatch(local, remote);
            if (reason is null) aligned++;
            else mismatches.Add((local, remote, reason));
        }

        foreach (var remote in exchangeOrders)
        {
            if (!localWithId.ContainsKey(remote.ExchangeOrderId))
                exchangeOnly.Add(remote);
        }

        // 5) 摘要
        Console.WriteLine("-- Summary --");
        Console.WriteLine($"  Aligned            : {aligned}");
        Console.WriteLine($"  Local-only         : {localOnly.Count}");
        Console.WriteLine($"  Exchange-only      : {exchangeOnly.Count}  {(exchangeOnly.Count > 0 ? "⚠️  GHOST ORDERS" : string.Empty)}");
        Console.WriteLine($"  Status/Qty mismatch: {mismatches.Count}");
        Console.WriteLine($"  Pending assign     : {pendingAssign.Count}");
        Console.WriteLine();

        if (pendingAssign.Count > 0)
        {
            Console.WriteLine("-- Pending assign (local Active without ExchangeOrderId) --");
            foreach (var o in pendingAssign)
                Console.WriteLine($"  OrderId={o.Id} Side={o.Side} Status={o.Status} Created={o.CreatedAt:HH:mm:ss}");
            Console.WriteLine();
        }

        if (localOnly.Count > 0)
        {
            Console.WriteLine("-- Local-only (local says Active, exchange has no such open order) --");
            foreach (var o in localOnly)
                Console.WriteLine(
                    $"  ExchangeOrderId={o.ExchangeOrderId} Side={o.Side} Status={o.Status}  " +
                    $"→ 建議 RefreshOrderStatusAsync / CancelOrderAsync 同步狀態");
            Console.WriteLine();
        }

        if (exchangeOnly.Count > 0)
        {
            Console.WriteLine("⚠️  -- Exchange-only (GHOST orders — not tracked locally) --");
            foreach (var r in exchangeOnly)
                Console.WriteLine(
                    $"  ExchangeOrderId={r.ExchangeOrderId} Side={r.Side} PosSide={r.PositionSide} " +
                    $"Qty={r.Quantity}/{r.QuantityFilled} Price={r.Price}  Status={r.Status} " +
                    $"Updated={r.UpdateTime:HH:mm:ss}  " +
                    $"→ 建議立即於交易所 UI 或 API 取消，並查清來源");
            Console.WriteLine();
        }

        if (mismatches.Count > 0)
        {
            Console.WriteLine("-- Status / Quantity mismatch --");
            foreach (var (local, remote, reason) in mismatches)
                Console.WriteLine(
                    $"  ExchangeOrderId={remote.ExchangeOrderId}  {reason}  " +
                    $"→ 建議 RefreshOrderStatusAsync 更新本地");
            Console.WriteLine();
        }

        // 6) 結論訊息
        if (localOnly.Count == 0 && exchangeOnly.Count == 0 && mismatches.Count == 0)
        {
            Console.WriteLine("✅ All Synced — 本地 Active 訂單與交易所掛單完全一致。");
            return 0;
        }

        if (exchangeOnly.Count > 0)
        {
            Console.WriteLine("❌ 偵測到幽靈訂單 — 請優先處理上方 ⚠️ 清單。");
            return 6;
        }

        return 0;
    }

    private static string? DiagnoseMismatch(Order local, ExchangeOpenOrderInfo remote)
    {
        // Status 不一致最常見：本地 New、交易所 PartiallyFilled；或本地 PartiallyFilled、交易所 Filled（已成交但 user-data WS 丟包）
        if (local.Status != remote.Status)
            return $"Status: local={local.Status} / exchange={remote.Status}";

        if (local.Quantity.Value != remote.Quantity)
            return $"Quantity: local={local.Quantity.Value} / exchange={remote.Quantity}";

        if (local.FilledQuantity.Value != remote.QuantityFilled)
            return $"Filled: local={local.FilledQuantity.Value} / exchange={remote.QuantityFilled}";

        return null;
    }
}
