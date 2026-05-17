using CryptoBot.Application.Backtesting.Search;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 參數搜索器 — 接收一組 <see cref="ParameterRange"/>，由可插拔的
/// <see cref="ISearchStrategy"/> 列舉要嘗試的組合，並對每個組合跑一次回測，
/// 最後彙總成排行榜。
///
/// 設計原則：
/// - Optimizer 只知道「參數組合 → <see cref="BacktestReport"/>」的契約（<paramref name="runOne"/> 委派），
///   不知道 Engine / Simulator / Store 是怎麼組裝的 — 那是呼叫端（CLI / Orchestrator）的職責。
///   這樣 Optimizer 完全住在 Application 層，不沾 Infrastructure。
/// - 搜尋演算法可插拔：「該掃哪些點」交給 <see cref="ISearchStrategy"/> — 預設 <see cref="GridSearchStrategy"/>
///   是重構前的笛卡兒積行為（向後相容）；<see cref="RandomSearchStrategy"/> 由 budget 控制次數，
///   未來新增 Bayesian / Genetic 等也只需 implement 抽象介面。
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
    /// 跑指定的搜尋演算法。
    /// </summary>
    /// <param name="ranges">參數範圍陣列</param>
    /// <param name="searchStrategy">搜尋演算法（如 <see cref="GridSearchStrategy"/> / <see cref="RandomSearchStrategy"/>）。</param>
    /// <param name="runOne">給定一組參數，回傳該組合的回測結果。必須 thread-safe（呼叫端負責 scope 隔離）。</param>
    /// <param name="maxDegreeOfParallelism">同時在跑的回測數；預設 <see cref="Environment.ProcessorCount"/>。</param>
    /// <param name="ct">取消權杖</param>
    /// <returns>依 <see cref="BacktestReport.NetPnL"/> 由高到低排序的結果清單。</returns>
    public async Task<IReadOnlyList<OptimizationRun>> RunAsync(
        IReadOnlyList<ParameterRange> ranges,
        ISearchStrategy searchStrategy,
        Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<BacktestReport>> runOne,
        int? maxDegreeOfParallelism = null,
        CancellationToken ct = default)
    {
        if (ranges.Count == 0)
            throw new ArgumentException("At least one parameter range is required.", nameof(ranges));

        var combinations = searchStrategy.Enumerate(ranges).ToList();
        var dop = maxDegreeOfParallelism ?? Math.Max(1, Environment.ProcessorCount);

        _logger.LogInformation(
            "🧪 [OPTIMIZE] {Count} combinations across {Ranges} dimensions via {Search}, DOP={Dop}",
            combinations.Count,
            ranges.Count,
            searchStrategy.GetType().Name,
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

        // S77 P0-1 fix: dedup by paramSet hash, 累積 TrialCount (Bayesian sampler 收斂後反覆 suggest 同 paramSet)
        // 對齊 ClaudeDesktop 2026-05-17 bug report「每一列必須對應唯一的參數組合 / 等價組合 ×N」
        return results
            .GroupBy(r => r.ParamSetHash())
            .Select(g => g.First() with { TrialCount = g.Count() })
            .OrderByDescending(r => r.Report.NetPnL)
            .ToList();
    }

    /// <summary>
    /// 向後相容 overload — 不指定搜尋演算法時預設使用 <see cref="GridSearchStrategy"/>，
    /// 行為與重構前完全一致。CLI 端 BacktestRunner 透過此呼叫保留現狀。
    /// </summary>
    public Task<IReadOnlyList<OptimizationRun>> RunAsync(
        IReadOnlyList<ParameterRange> ranges,
        Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<BacktestReport>> runOne,
        int? maxDegreeOfParallelism = null,
        CancellationToken ct = default)
        => RunAsync(ranges, new GridSearchStrategy(), runOne, maxDegreeOfParallelism, ct);

    /// <summary>
    /// S69 — 自適應搜尋的 RunAsync 路徑。與 ISearchStrategy 的「先列舉再並行」模型不同：
    /// 本 overload 序列執行 budget 次 (suggest → run → report) 三步循環，讓 sampler 從上一輪結果學習。
    ///
    /// 為什麼不能並行：sampler 的下一組推薦依賴所有「已 tell 完成」的 trial。若多個 worker 同時 ask
    /// 而結果還沒 tell 回去，會吃到 stale state。Optuna 自身支援多 worker 模式但需額外協調機制 —
    /// Phase 2 先用最簡的序列模型，未來如需並行再迭代。
    /// </summary>
    public async Task<IReadOnlyList<OptimizationRun>> RunAsync(
        IReadOnlyList<ParameterRange> ranges,
        IAdaptiveSearchStrategy strategy,
        int budget,
        Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<BacktestReport>> runOne,
        CancellationToken ct = default)
    {
        if (ranges.Count == 0)
            throw new ArgumentException("At least one parameter range is required.", nameof(ranges));
        if (budget <= 0)
            throw new ArgumentException($"Budget must be positive: {budget}", nameof(budget));

        await strategy.InitializeAsync(ranges, budget, ct).ConfigureAwait(false);

        var results = new List<OptimizationRun>(budget);

        try
        {
            _logger.LogInformation(
                "🧪 [OPTIMIZE-ADAPTIVE] budget={Budget} via {Strategy}",
                budget, strategy.GetType().Name);

            for (var i = 0; i < budget; i++)
            {
                ct.ThrowIfCancellationRequested();

                var paramSet = await strategy.SuggestNextAsync(ct).ConfigureAwait(false);

                BacktestReport report;
                try
                {
                    report = await runOne(paramSet, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "🧪 [OPTIMIZE-ADAPTIVE] Run failed for params={Params}",
                        FormatParams(paramSet));
                    // 失敗 trial 仍要回報 sampler — 用 decimal.MinValue 與真實負 PnL 區分（合法 PnL 不會這麼極端）。
                    await strategy.ReportResultAsync(paramSet, decimal.MinValue, ct).ConfigureAwait(false);
                    continue;
                }

                results.Add(new OptimizationRun(paramSet, report));

                _logger.LogInformation(
                    "🧪 [OPTIMIZE-ADAPTIVE] {Done}/{Total} params={Params} pnl={PnL:F2} dd={DD:F2}% fills={Fills}",
                    i + 1, budget, FormatParams(paramSet),
                    report.NetPnL, report.MaxDrawdownPercent, report.OrdersFilled);

                await strategy.ReportResultAsync(paramSet, report.NetPnL, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await strategy.DisposeAsync().ConfigureAwait(false);
        }

        // S77 P0-1 fix: dedup by paramSet hash, 累積 TrialCount (Bayesian sampler 收斂後反覆 suggest 同 paramSet)
        // 對齊 ClaudeDesktop 2026-05-17 bug report「每一列必須對應唯一的參數組合 / 等價組合 ×N」
        return results
            .GroupBy(r => r.ParamSetHash())
            .Select(g => g.First() with { TrialCount = g.Count() })
            .OrderByDescending(r => r.Report.NetPnL)
            .ToList();
    }

    private static string FormatParams(IReadOnlyDictionary<string, decimal> p) =>
        string.Join(", ", p.Select(kv => $"{kv.Key}={kv.Value}"));
}
