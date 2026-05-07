using System.Globalization;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S71-B Probe Trades — 直接對 BingX user trades 端點抓真實成交紀錄。
///
/// <para>
/// 三類使用情境（依 IM §S72 + IM §L8 領域對照表）：
/// </para>
/// <list type="number">
///   <item>殭屍單清理：拿到真實終態（Filled / Canceled / Expired）後寫 SQL UPDATE</item>
///   <item>PnL 對帳：判讀 Dashboard 顯示值與交易所明細手算之間的差異（IM §S70 規範）</item>
///   <item>跨 session 復原：補殺 user-data WS 漏接的最終 fill update</item>
/// </list>
///
/// <para>對齊紀律：</para>
/// <list type="bullet">
///   <item><see cref="IExchangeClient.GetTradeHistoryAsync"/>（S72 引入；BingXExchangeClient 內部走 SDK <c>GetUserTradesAsync</c> 靜態呼叫，IRON ⑧）</item>
///   <item>時間欄一律轉 Asia/Taipei 顯示（IM §S70 規範 — DB 存 UTC、UI 顯示在地）</item>
///   <item>輸出含 BingX 原始 TradeId / OrderId（IRON ⑫ 寫真單原則 — 證據鏈可重建）</item>
///   <item>含中文字串 → 本檔必含 UTF-8 BOM（IRON ⑩ + IM §L3）</item>
/// </list>
///
/// <para>
/// <b>本指令純讀</b>，不對 DB 或交易所做任何寫入。可在 Demo / Live 任一模式安全執行。
/// </para>
/// </summary>
public sealed class ProbeTradesCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ProbeTradesCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "probe-trades";
    public IReadOnlyList<string> Aliases => new[] { "trades", "history" };
    public string Description => "Query exchange user trades for evidence-based reconciliation (read-only).";
    public string Usage => "probe-trades <Symbol> [DaysAgo|YYYY-MM-DD]";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            Console.Error.WriteLine("  Time arg defaults to 7 (= last 7 days). Pass an integer (days ago) or YYYY-MM-DD (Asia/Taipei midnight).");
            return 2;
        }

        Symbol symbol;
        try { symbol = Symbol.Parse(args[0]); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid symbol '{args[0]}': {ex.Message}");
            return 2;
        }

        DateTime sinceUtc;
        try
        {
            sinceUtc = ParseSinceUtc(args.Length >= 2 ? args[1] : "7");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid time arg '{args[1]}': {ex.Message}");
            return 2;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var exchange = sp.GetRequiredService<IExchangeClient>();

        var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var sinceLocal = TimeZoneInfo.ConvertTimeFromUtc(sinceUtc, taipei);

        Console.WriteLine("=== Probe Trades (BingX user trades) ===");
        Console.WriteLine($"Symbol : {symbol.BingXFormat}");
        Console.WriteLine($"Mode   : {exchange.CurrentMode}");
        Console.WriteLine($"Since  : {sinceLocal:yyyy-MM-dd HH:mm:ss} (Asia/Taipei)  /  {sinceUtc:yyyy-MM-dd HH:mm:ss}Z (UTC)");
        Console.WriteLine();

        IReadOnlyList<ExchangeTradeInfo> trades;
        try
        {
            trades = await exchange.GetTradeHistoryAsync(symbol, sinceUtc, until: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] GetTradeHistoryAsync failed: {ex.Message}");
            return 4;
        }

        if (trades.Count == 0)
        {
            Console.WriteLine("(no trades found in this window)");
            Console.WriteLine();
            Console.WriteLine("→ 解讀：該時間窗內 BingX 端無對應 user trade。可能原因：");
            Console.WriteLine("    a) 時間窗太窄 — 試擴大 DaysAgo");
            Console.WriteLine("    b) Symbol 從未交易 — 對照 strategies / sync-orders 確認");
            Console.WriteLine("    c) Mode 錯誤 — 確認 env 是 Demo / Live 中正確一邊");
            return 0;
        }

        // 表格輸出（時間欄轉 Asia/Taipei；其他欄保留原值）
        Console.WriteLine("-- Trades --");
        Console.WriteLine(
            $"  {"Time (Asia/Taipei)",-22}  {"Side",-7} {"PosSide",-8} " +
            $"{"Qty",12} {"Price",14} {"Commission",14} {"RealizedPnL",14}  TradeId / OrderId");
        Console.WriteLine(
            $"  {new string('-', 22)}  {new string('-', 7)} {new string('-', 8)} " +
            $"{new string('-', 12)} {new string('-', 14)} {new string('-', 14)} {new string('-', 14)}  " +
            $"{new string('-', 30)}");

        foreach (var t in trades.OrderBy(x => x.Time))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(t.Time, taipei);
            Console.WriteLine(
                $"  {local:yyyy-MM-dd HH:mm:ss}    " +
                $"{t.Side,-7} {t.PositionSide,-8} " +
                $"{t.Quantity,12:F4} {t.Price,14:F4} {t.Commission,14:F6} {t.RealizedPnl,14:F4}  " +
                $"{t.TradeId} / {t.OrderId}");
        }

        // 聚合計算（加權平均價 + 總手續費 + 總實現損益）
        // 加權平均：sum(Px * Qty) / sum(Qty)；嚴禁用算術平均（IRON ① decimal 精度）
        Console.WriteLine();
        Console.WriteLine("-- Aggregate --");

        var totalQty = trades.Sum(t => t.Quantity);
        decimal vwap = 0m;
        if (totalQty > 0m)
        {
            vwap = trades.Sum(t => t.Price * t.Quantity) / totalQty;
        }
        var totalCommission = trades.Sum(t => t.Commission);
        var totalRealized = trades.Sum(t => t.RealizedPnl);

        Console.WriteLine($"  Trades count     : {trades.Count}");
        Console.WriteLine($"  Total Quantity   : {totalQty:F4}");
        Console.WriteLine($"  Weighted Avg Px  : {vwap:F4}");
        Console.WriteLine($"  Total Commission : {totalCommission:F6}");
        Console.WriteLine($"  Total RealizedPnL: {totalRealized:F4}");
        Console.WriteLine();
        Console.WriteLine($"→ 對帳建議：本 RealizedPnL 與 Dashboard `TodayRealizedPnL` 比對前，先過濾窗口（IM §S70 時區規範）。");

        return 0;
    }

    /// <summary>
    /// 解析時間參數：純整數 (天數) 或 <c>YYYY-MM-DD</c> (Asia/Taipei 本地日期之 00:00)。
    /// 都轉成 UTC since 回傳，以對齊 <see cref="IExchangeClient.GetTradeHistoryAsync"/> 預期的 UTC 入參。
    /// </summary>
    /// <remarks>
    /// 為何不接受純時間 (HH:mm:ss)：本指令是「歷史對帳」用途、最小粒度為日；若需更細粒度應走 SQL 直查 Orders 表。
    /// </remarks>
    internal static DateTime ParseSinceUtc(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            throw new ArgumentException("Empty time arg.");

        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            if (days < 0)
                throw new ArgumentException("Days ago must be non-negative.");
            // 取「N 天前的此刻」— 不對齊日界，呼叫端可自行 grep
            return DateTime.UtcNow - TimeSpan.FromDays(days);
        }

        if (DateTime.TryParseExact(
                arg, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var localDate))
        {
            // localDate 是 unspecified；視為 Asia/Taipei 當日 00:00，轉 UTC
            var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
            var localUnspecified = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(localUnspecified, taipei);
        }

        throw new ArgumentException($"Cannot parse '{arg}' as integer (days ago) or YYYY-MM-DD date.");
    }
}
