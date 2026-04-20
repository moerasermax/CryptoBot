using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 參數網格搜索器 — 把多個 <see cref="ParameterRange"/> 展開成笛卡兒積，
/// 為每個組合跑一次回測，彙總成排行榜。
///
/// 設計原則：
/// - Optimizer 只知道「參數組合 → <see cref="BacktestReport"/>」的契約（<paramref name="runOne"/> 委派），
///   不知道 Engine / Simulator / Store 是怎麼組裝的 — 那是呼叫端（CLI）的職責。
///   這樣 Optimizer 完全住在 Application 層，不沾 Infrastructure。
/// - 並行：用 <see cref="Parallel.ForEachAsync{TSource}"/> 配 <see cref="ParallelOptions.MaxDegreeOfParallelism"/>
///   控制同時在跑的回測數；單次回測內部是單執行緒，任兩個 run 之間不共享狀態（各自 new 出自己的 Simulator）。
///
/// 呼叫端必須確保 <paramref name="runOne"/> 每次呼叫都建立全新的 Simulator / DbContext scope，
/// 否則多執行緒共用 EF DbContext 會爆。
/// </summary>
public sealed class StrategyOptimizer
{
    private readonly ILogger<StrategyOptimizer> _logger;

    public StrategyOptimizer(ILogger<StrategyOptimizer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 跑網格搜索。
    /// </summary>
    /// <param name="ranges">參數範圍陣列</param>
    /// <param name="runOne">給定一組參數，回傳該組合的回測結果。必須 thread-safe（呼叫端負責 scope 隔離）。</param>
    /// <param name="maxDegreeOfParallelism">同時在跑的回測數；預設 <see cref="Environment.ProcessorCount"/>。</param>
    /// <param name="ct">取消權杖</param>
    /// <returns>依 <see cref="BacktestReport.NetPnL"/> 由高到低排序的結果清單。</returns>
    public async Task<IReadOnlyList<OptimizationRun>> RunAsync(
        IReadOnlyList<ParameterRange> ranges,
        Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<BacktestReport>> runOne,
        int? maxDegreeOfParallelism = null,
        CancellationToken ct = default)
    {
        if (ranges.Count == 0)
            throw new ArgumentException("At least one parameter range is required.", nameof(ranges));

        var combinations = CartesianProduct(ranges).ToList();
        var dop = maxDegreeOfParallelism ?? Math.Max(1, Environment.ProcessorCount);

        _logger.LogInformation(
            "🧪 [OPTIMIZE] {Count} combinations across {Ranges} dimensions, DOP={Dop}",
            combinations.Count,
            ranges.Count,
            dop);

        var results = new System.Collections.Concurrent.ConcurrentBag<OptimizationRun>();
        var completed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(combinations, options, async (combo, token) =>
        {
            try
            {
                var report = await runOne(combo, token).ConfigureAwait(false);
                results.Add(new OptimizationRun(combo, report));

                var done = Interlocked.Increment(ref completed);
                _logger.LogInformation(
                    "🧪 [OPTIMIZE] {Done}/{Total} params={Params} pnl={PnL:F2} dd={DD:F2}% fills={Fills}",
                    done, combinations.Count, FormatParams(combo),
                    report.NetPnL, report.MaxDrawdownPercent, report.OrdersFilled);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🧪 [OPTIMIZE] Run failed for params={Params}", FormatParams(combo));
            }
        }).ConfigureAwait(false);

        return results
            .OrderByDescending(r => r.Report.NetPnL)
            .ToList();
    }

    private static IEnumerable<IReadOnlyDictionary<string, decimal>> CartesianProduct(
        IReadOnlyList<ParameterRange> ranges)
    {
        IEnumerable<IReadOnlyDictionary<string, decimal>> seed = new[]
        {
            (IReadOnlyDictionary<string, decimal>)new Dictionary<string, decimal>()
        };

        foreach (var range in ranges)
        {
            var captured = range;
            seed = seed.SelectMany(
                prefix => captured.Enumerate(),
                (prefix, value) =>
                {
                    var next = new Dictionary<string, decimal>(prefix) { [captured.Name] = value };
                    return (IReadOnlyDictionary<string, decimal>)next;
                });
        }

        return seed;
    }

    private static string FormatParams(IReadOnlyDictionary<string, decimal> p) =>
        string.Join(", ", p.Select(kv => $"{kv.Key}={kv.Value}"));
}
