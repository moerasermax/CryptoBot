using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Backtesting.Search;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Backtesting.Search;

/// <summary>
/// S67 — 確認 <see cref="StrategyOptimizer"/> 確實透過 <see cref="ISearchStrategy"/>
/// 取得組合，而不是寫死走笛卡兒積：
/// 1) 注入 <see cref="RandomSearchStrategy"/> 時，runOne 被呼叫的次數 = budget；
/// 2) 不傳 search 時走向後相容 overload（=Grid），總呼叫次數 = 笛卡兒積大小。
/// </summary>
public class StrategyOptimizerSearchTests
{
    [Fact]
    public async Task RunAsync_WithRandomSearch_RunsExactlyBudgetTimes()
    {
        var ranges = new[]
        {
            new ParameterRange("A", Min: 1m, Max: 10m, Step: 1m),  // 10 vals
            new ParameterRange("B", Min: 1m, Max: 10m, Step: 1m),  // 10 vals → grid = 100
        };

        var calls = 0;
        var optimizer = new StrategyOptimizer(NullLogger<StrategyOptimizer>.Instance);

        var runs = await optimizer.RunAsync(
            ranges,
            new RandomSearchStrategy(budget: 17, seed: 99),
            runOne: (paramSet, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(MakeReport(paramSet["A"]));
            },
            maxDegreeOfParallelism: 1);

        Assert.Equal(17, calls);
        // S77 P0-1: dedup by paramSet hash 後 runs.Count 可能 < budget (RandomSearch with-replacement 偶有重複 paramSet),
        // 但 TrialCount sum 必須對應原 trial 數
        Assert.True(runs.Count <= 17, $"runs.Count {runs.Count} should be <= 17");
        Assert.Equal(17, runs.Sum(r => r.TrialCount));
    }

    [Fact]
    public async Task RunAsync_BackwardCompatOverload_DefaultsToGridCartesian()
    {
        var ranges = new[]
        {
            new ParameterRange("A", Min: 1m, Max: 3m, Step: 1m),  // 3 vals
            new ParameterRange("B", Min: 1m, Max: 4m, Step: 1m),  // 4 vals → grid = 12
        };

        var calls = 0;
        var optimizer = new StrategyOptimizer(NullLogger<StrategyOptimizer>.Instance);

        // 不傳 ISearchStrategy — 走向後相容 overload，行為應與重構前一致（笛卡兒積）。
        var runs = await optimizer.RunAsync(
            ranges,
            runOne: (paramSet, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(MakeReport(paramSet["A"]));
            },
            maxDegreeOfParallelism: 1);

        Assert.Equal(12, calls);
        Assert.Equal(12, runs.Count);
    }

    [Fact]
    public async Task RunAsync_SortsResultsByNetPnLDesc()
    {
        var ranges = new[] { new ParameterRange("X", Min: 1m, Max: 5m, Step: 1m) };
        var optimizer = new StrategyOptimizer(NullLogger<StrategyOptimizer>.Instance);

        // PnL = X — 越大越好；Optimizer 應按 NetPnL desc 排序。
        var runs = await optimizer.RunAsync(
            ranges,
            new GridSearchStrategy(),
            runOne: (paramSet, _) => Task.FromResult(MakeReport(paramSet["X"])),
            maxDegreeOfParallelism: 1);

        Assert.Equal(5, runs.Count);
        for (var i = 0; i < runs.Count - 1; i++)
        {
            Assert.True(runs[i].Report.NetPnL >= runs[i + 1].Report.NetPnL);
        }
        Assert.Equal(5m, runs[0].Report.NetPnL);
    }

    private static BacktestReport MakeReport(decimal pnl) => new(
        TotalKlines: 0,
        SignalsTriggered: 0,
        OrdersFilled: 5,
        StartingBalance: 10_000m,
        EndingBalance: 10_000m + pnl,
        PeakEquity: 10_000m + pnl,
        MaxDrawdownPercent: 0m,
        FirstKlineTime: null,
        LastKlineTime: null,
        Fills: Array.Empty<Order>(),
        EquityCurve: Array.Empty<EquityPoint>());
}
