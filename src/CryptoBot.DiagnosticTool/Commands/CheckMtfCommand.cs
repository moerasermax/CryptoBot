using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S63 Phase 1：多週期時間軸對齊診斷。
///
/// 同時抓 15m / 1H / 4H 的最近 K 線，並以「已收盤 / 進行中」明確標註尾根 — 讓 PM 人工肉眼核對：
///   1) 15m 的 OpenTime 是否嚴格包覆在對應 1H / 4H 區間內；
///   2) 交易所回傳的最末根是否真的是「未收盤」，避免 MTF 策略取到未來函數；
///   3) 各週期 Close 價格的跨時空一致性。
///
/// 本指令只讀 REST，不訂閱 WS；跑完即退。
/// </summary>
public sealed class CheckMtfCommand : IDiagnosticCommand
{
    private static readonly KlineInterval[] TargetIntervals =
    {
        KlineInterval.FifteenMinutes,
        KlineInterval.OneHour,
        KlineInterval.FourHours,
    };

    private const int RequestLimit = 6;   // 至少抓 3 根已收盤 + 1 根進行中，留一點緩衝
    private const int DisplayClosed = 3;  // 依膠囊要求印「最近 3 組」已收盤

    private readonly IServiceScopeFactory _scopeFactory;

    public CheckMtfCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "s63_check-mtf";
    public IReadOnlyList<string> Aliases => new[] { "check-mtf", "mtf" };
    public string Description => "Fetch 15m/1H/4H klines in parallel and print aligned timestamps + closes (VCP-1).";
    public string Usage => "s63_check-mtf <Symbol>   e.g. 's63_check-mtf BTC-USDT'";

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
            Console.Error.WriteLine($"Invalid symbol: {args[0]} — {ex.Message}");
            return 2;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var exchange = scope.ServiceProvider.GetRequiredService<IExchangeClient>();

        var fetchedAt = DateTime.UtcNow;
        Console.WriteLine("=== Multi-Timeframe Alignment Check ===");
        Console.WriteLine($"Symbol    : {symbol.BingXFormat}");
        Console.WriteLine($"Mode      : {exchange.CurrentMode}");
        Console.WriteLine($"Fetched at: {fetchedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine();

        // 並行抓三個週期 — 若某支失敗，個別回報，其餘繼續呈現（避免單一週期故障把整份巡檢蓋掉）。
        var tasks = TargetIntervals
            .Select(iv => (iv, task: FetchSafeAsync(exchange, symbol, iv, ct)))
            .ToArray();

        foreach (var (iv, task) in tasks)
            await task.ConfigureAwait(false);

        // 依膠囊要求印「最近 3 組對齊的時間戳與收盤價」— 最新那根特別標「進行中」。
        foreach (var (iv, task) in tasks)
        {
            Console.WriteLine($"-- {IntervalLabel(iv)} --");
            var result = task.Result;
            if (result.Error is not null)
            {
                Console.WriteLine($"  [FETCH FAIL] {result.Error}");
                Console.WriteLine();
                continue;
            }

            var klines = result.Klines!;
            if (klines.Count == 0)
            {
                Console.WriteLine("  (empty response)");
                Console.WriteLine();
                continue;
            }

            // 交易所回傳一般是舊→新，保險起見以 OpenTime 排序。
            var sorted = klines.OrderBy(k => k.OpenTime).ToList();
            // 最後一根視為「進行中」：若 fetchedAt < CloseTime 則確定未收盤。
            var last = sorted[^1];
            bool lastInProgress = fetchedAt < last.CloseTime;

            // 列印最後 DisplayClosed 根已收盤 K 線：從倒數第 2 根往前取（若最末根在進行中），否則直接取尾 3 根。
            var startForClosed = lastInProgress ? sorted.Count - 1 : sorted.Count;
            var closedSlice = sorted
                .Take(startForClosed)
                .Skip(Math.Max(0, startForClosed - DisplayClosed))
                .ToList();

            foreach (var k in closedSlice)
            {
                Console.WriteLine(
                    $"  {k.OpenTime:yyyy-MM-dd HH:mm} → {k.CloseTime:HH:mm}  " +
                    $"Close={k.Close,10:F4}  [closed]");
            }
            if (lastInProgress)
            {
                Console.WriteLine(
                    $"  {last.OpenTime:yyyy-MM-dd HH:mm} → {last.CloseTime:HH:mm}  " +
                    $"Close={last.Close,10:F4}  [IN PROGRESS — DO NOT use for signals]");
            }
            Console.WriteLine();
        }

        // 對齊自檢：最新 15m OpenTime 必須落在最新 1H 的 [Open, Close) 內、同時 1H 必須包覆在 4H 內。
        PrintAlignmentSanity(tasks, fetchedAt);
        return 0;
    }

    private static void PrintAlignmentSanity(
        (KlineInterval iv, Task<FetchResult> task)[] tasks, DateTime fetchedAt)
    {
        Kline? k15 = tasks.First(t => t.iv == KlineInterval.FifteenMinutes).task.Result.Klines?.LastOrDefault();
        Kline? k1h = tasks.First(t => t.iv == KlineInterval.OneHour).task.Result.Klines?.LastOrDefault();
        Kline? k4h = tasks.First(t => t.iv == KlineInterval.FourHours).task.Result.Klines?.LastOrDefault();

        Console.WriteLine("-- Alignment sanity --");
        if (k15 is null || k1h is null || k4h is null)
        {
            Console.WriteLine("  (skipped — one or more intervals failed to fetch)");
            return;
        }

        var ok15in1h = k15.OpenTime >= k1h.OpenTime && k15.OpenTime < k1h.CloseTime;
        var ok1hin4h = k1h.OpenTime >= k4h.OpenTime && k1h.OpenTime < k4h.CloseTime;

        Console.WriteLine($"  最新 15m OpenTime {k15.OpenTime:HH:mm} 落在 1H [{k1h.OpenTime:HH:mm},{k1h.CloseTime:HH:mm})  → {(ok15in1h ? "OK" : "MISALIGNED")}");
        Console.WriteLine($"  最新 1H  OpenTime {k1h.OpenTime:HH:mm} 落在 4H [{k4h.OpenTime:HH:mm},{k4h.CloseTime:HH:mm})  → {(ok1hin4h ? "OK" : "MISALIGNED")}");
        Console.WriteLine($"  Fetched {fetchedAt:HH:mm:ss} 與各週期 CloseTime 關係（證明「進行中」判定）：");
        Console.WriteLine($"    15m CloseTime = {k15.CloseTime:HH:mm}   in-progress? {fetchedAt < k15.CloseTime}");
        Console.WriteLine($"    1H  CloseTime = {k1h.CloseTime:HH:mm}   in-progress? {fetchedAt < k1h.CloseTime}");
        Console.WriteLine($"    4H  CloseTime = {k4h.CloseTime:HH:mm}   in-progress? {fetchedAt < k4h.CloseTime}");
    }

    private static async Task<FetchResult> FetchSafeAsync(
        IExchangeClient exchange, Symbol symbol, KlineInterval interval, CancellationToken ct)
    {
        try
        {
            var klines = await exchange.GetKlinesAsync(symbol, interval, limit: RequestLimit, ct: ct)
                .ConfigureAwait(false);
            return new FetchResult(klines, null);
        }
        catch (Exception ex)
        {
            return new FetchResult(null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string IntervalLabel(KlineInterval iv) => iv switch
    {
        KlineInterval.FifteenMinutes => "15m",
        KlineInterval.OneHour => "1H",
        KlineInterval.FourHours => "4H",
        _ => iv.ToString(),
    };

    private sealed record FetchResult(IReadOnlyList<Kline>? Klines, string? Error);
}
