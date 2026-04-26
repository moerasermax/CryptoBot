using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S60 K 線完整性巡檢：比對本地 <see cref="IHistoricalKlineStore"/> 與交易所 REST 最新 N 根。
///
/// 產出三類結果：
///   - <b>Aligned</b>：OpenTime 對應且 OHLCV 完全一致。
///   - <b>Gap</b>：交易所有，本地缺。
///   - <b>Mismatch</b>：雙方都有該 OpenTime，但 OHLC 任一欄位不同（價格歧異，通常是歷史被竄改或下載中斷腐蝕）。
///
/// 加 <c>--fix</c> 時，對每一筆 Gap/Mismatch 呼叫 <see cref="IHistoricalKlineStore.UpsertAsync"/>
/// 用交易所版本覆蓋本地 — 語意冪等，不會重複插入。
///
/// 進行中尾根（CloseTime 在 UtcNow 之後）永遠先切除，避免把未收盤狀態的值當基準比對出虛假 Mismatch。
/// </summary>
public sealed class CheckKlineCommand : IDiagnosticCommand
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;

    private readonly IServiceScopeFactory _scopeFactory;

    public CheckKlineCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "s60_check-kline";
    public IReadOnlyList<string> Aliases => new[] { "check-kline", "kline" };
    public string Description => "Compare local HistoricalKlineStore with exchange latest N candles; --fix auto-upserts gaps/mismatches.";
    public string Usage => "s60_check-kline <Symbol> <Interval> [Limit] [--fix]";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            Console.Error.WriteLine("  Interval examples: 15m / 1h / 4h / FifteenMinutes / OneHour / FourHours");
            return 2;
        }

        Symbol symbol;
        try { symbol = Symbol.Parse(args[0]); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid symbol '{args[0]}': {ex.Message}");
            return 2;
        }

        if (!TryParseInterval(args[1], out var interval))
        {
            Console.Error.WriteLine($"Invalid interval '{args[1]}'. Try 15m, 1h, 4h or enum name like FifteenMinutes.");
            return 2;
        }

        var limit = DefaultLimit;
        bool fix = args.Any(a => string.Equals(a, "--fix", StringComparison.OrdinalIgnoreCase));

        // 在 Symbol/Interval 之後、--fix 之外找出純數字當 limit
        foreach (var a in args.Skip(2))
        {
            if (a.StartsWith("--")) continue;
            if (int.TryParse(a, out var n))
            {
                if (n <= 0 || n > MaxLimit)
                {
                    Console.Error.WriteLine($"Limit must be 1..{MaxLimit}, got {n}.");
                    return 2;
                }
                limit = n;
            }
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var exchange = sp.GetRequiredService<IExchangeClient>();
        var store = sp.GetRequiredService<IHistoricalKlineStore>();

        Console.WriteLine("=== Kline Integrity Check ===");
        Console.WriteLine($"Symbol   : {symbol.BingXFormat}");
        Console.WriteLine($"Interval : {interval}");
        Console.WriteLine($"Limit    : {limit}");
        Console.WriteLine($"Fix mode : {(fix ? "ON (will UPSERT gaps & mismatches)" : "off (dry-run)")}");
        Console.WriteLine();

        // 1) 抓交易所，切進行中尾根
        IReadOnlyList<Kline> exchangeKlines;
        try
        {
            exchangeKlines = await exchange.GetKlinesAsync(symbol, interval, limit: limit, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] Fetch exchange klines failed: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }

        var exchangeClosed = TrimInProgressTail(exchangeKlines);
        if (exchangeClosed.Count == 0)
        {
            Console.WriteLine("Exchange returned no closed klines — nothing to check.");
            return 0;
        }

        var earliest = exchangeClosed[0].OpenTime;
        var latest = exchangeClosed[^1].OpenTime;
        var tailBuffer = interval.ToTimeSpan();
        Console.WriteLine($"Exchange : {exchangeClosed.Count} closed klines, {earliest:yyyy-MM-dd HH:mm} → {latest:yyyy-MM-dd HH:mm}");

        // 2) 本地同範圍（多加一個 interval 的 buffer 保守讀，以免 StreamRange 因邊界條件漏）
        var localByOpen = new Dictionary<DateTime, Kline>();
        await foreach (var k in store.StreamRangeAsync(symbol, interval,
                           earliest.AddTicks(-1), latest + tailBuffer, ct).ConfigureAwait(false))
        {
            localByOpen[k.OpenTime] = k;
        }
        Console.WriteLine($"Local    : {localByOpen.Count} klines in the same range");
        Console.WriteLine();

        // 3) 對齊比對
        var gaps = new List<Kline>();
        var mismatches = new List<(Kline Exchange, Kline Local)>();
        int aligned = 0;

        foreach (var ex in exchangeClosed)
        {
            if (!localByOpen.TryGetValue(ex.OpenTime, out var local))
            {
                gaps.Add(ex);
                continue;
            }

            if (IsDifferent(ex, local))
                mismatches.Add((ex, local));
            else
                aligned++;
        }

        // 4) 輸出報告
        Console.WriteLine("-- Summary --");
        Console.WriteLine($"  Aligned    : {aligned}");
        Console.WriteLine($"  Gap        : {gaps.Count}");
        Console.WriteLine($"  Mismatch   : {mismatches.Count}");
        Console.WriteLine();

        if (gaps.Count > 0)
        {
            Console.WriteLine("-- Gap (exchange has, local missing) --");
            foreach (var g in gaps.Take(20))
                Console.WriteLine($"  {g.OpenTime:yyyy-MM-dd HH:mm}  Close={g.Close:F4}");
            if (gaps.Count > 20) Console.WriteLine($"  ... ({gaps.Count - 20} more)");
            Console.WriteLine();
        }

        if (mismatches.Count > 0)
        {
            Console.WriteLine("-- Mismatch (both sides have but fields differ) --");
            foreach (var (ex, local) in mismatches.Take(20))
                Console.WriteLine(
                    $"  {ex.OpenTime:yyyy-MM-dd HH:mm}  " +
                    $"ex(O={ex.Open:F4} H={ex.High:F4} L={ex.Low:F4} C={ex.Close:F4} V={ex.Volume:F4})  " +
                    $"local(O={local.Open:F4} H={local.High:F4} L={local.Low:F4} C={local.Close:F4} V={local.Volume:F4})");
            if (mismatches.Count > 20) Console.WriteLine($"  ... ({mismatches.Count - 20} more)");
            Console.WriteLine();
        }

        if (gaps.Count == 0 && mismatches.Count == 0)
        {
            Console.WriteLine("✅ All Aligned — 本地庫存與交易所最新 N 根完全一致。");
            return 0;
        }

        // 5) --fix
        if (!fix)
        {
            Console.WriteLine("⚠️  Re-run with --fix to upsert exchange values and heal the local store.");
            return 0;
        }

        var toFix = new List<Kline>(gaps.Count + mismatches.Count);
        toFix.AddRange(gaps);
        foreach (var (ex, _) in mismatches) toFix.Add(ex);

        try
        {
            await store.UpsertAsync(symbol, interval, toFix, ct).ConfigureAwait(false);
            Console.WriteLine($"✅ Healed {toFix.Count} rows (gaps={gaps.Count}, mismatches={mismatches.Count}).");
            Console.WriteLine("   Re-run without --fix to confirm 'All Aligned'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] Upsert failed: {ex.GetType().Name}: {ex.Message}");
            return 5;
        }
    }

    private static bool IsDifferent(Kline a, Kline b)
    {
        // OHLC 與 Volume 必須完全一致。decimal 比對無浮點誤差，直接 ==。
        // 允許 CloseTime 差異（某些歷史版本可能存成 OpenTime+1s 的舊資料 — S63-HOTFIX 之後才正確），
        // 為避免老 DB 條目全部被當成 mismatch 騷擾，CloseTime 不入比對。
        return a.Open != b.Open
            || a.High != b.High
            || a.Low != b.Low
            || a.Close != b.Close
            || a.Volume != b.Volume;
    }

    private static IReadOnlyList<Kline> TrimInProgressTail(IReadOnlyList<Kline> klines)
    {
        if (klines.Count == 0) return klines;
        var sorted = klines.OrderBy(k => k.OpenTime).ToList();
        var last = sorted[^1];
        if (last.CloseTime > DateTime.UtcNow)
        {
            if (sorted.Count == 1) return Array.Empty<Kline>();
            sorted.RemoveAt(sorted.Count - 1);
        }
        return sorted;
    }

    private static bool TryParseInterval(string input, out KlineInterval result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        // 常見別名（大小寫不敏感）
        var alias = input.Trim().ToLowerInvariant() switch
        {
            "1m" => KlineInterval.OneMinute,
            "3m" => KlineInterval.ThreeMinutes,
            "5m" => KlineInterval.FiveMinutes,
            "15m" => KlineInterval.FifteenMinutes,
            "30m" => KlineInterval.ThirtyMinutes,
            "1h" => KlineInterval.OneHour,
            "2h" => KlineInterval.TwoHours,
            "4h" => KlineInterval.FourHours,
            "6h" => KlineInterval.SixHours,
            "8h" => KlineInterval.EightHours,
            "12h" => KlineInterval.TwelveHours,
            "1d" or "d" => KlineInterval.OneDay,
            "3d" => KlineInterval.ThreeDays,
            "1w" or "w" => KlineInterval.OneWeek,
            "1mo" or "mo" => KlineInterval.OneMonth,
            _ => (KlineInterval?)null,
        };
        if (alias.HasValue)
        {
            result = alias.Value;
            return true;
        }

        // enum 原名（FifteenMinutes 等）
        return Enum.TryParse(input, ignoreCase: true, out result);
    }
}
