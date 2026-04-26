using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Backtesting.Search;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Backtesting.Search;

/// <summary>
/// S69 — 自適應搜尋的 RunAsync overload 行為驗證。
/// 用 fake <see cref="IAdaptiveSearchStrategy"/>（無 HTTP）驗 Optimizer 端的協議：
/// 1) Init → (suggest → run → report) × budget → Dispose 順序正確
/// 2) report 收到的是 runOne 回傳的 NetPnL
/// 3) 結果按 NetPnL desc 排序
/// 4) runOne 拋例外時走 decimal.MinValue 補報，不中斷整個 budget
/// </summary>
public class StrategyOptimizerAdaptiveTests
{
    [Fact]
    public async Task RunAsync_Adaptive_FollowsSuggestRunReportLoop()
    {
        var ranges = new[] { new ParameterRange("X", Min: 1m, Max: 10m, Step: 1m) };
        var fake = new FakeAdaptiveStrategy(suggestedXValues: new[] { 3m, 7m, 5m });
        var optimizer = new StrategyOptimizer(NullLogger<StrategyOptimizer>.Instance);

        var runs = await optimizer.RunAsync(
            ranges,
            fake,
            budget: 3,
            runOne: (paramSet, _) => Task.FromResult(MakeReport(paramSet["X"])));

        Assert.True(fake.Initialized);
        Assert.Equal(3, fake.SuggestCount);
        Assert.Equal(3, fake.ReportedValues.Count);
        Assert.True(fake.Disposed);

        // ReportedValues 應為 [3, 7, 5]（runOne 用 X 當 PnL）
        Assert.Equal(new[] { 3m, 7m, 5m }, fake.ReportedValues.ToArray());

        // 結果應排序為 7, 5, 3（NetPnL desc）
        Assert.Equal(3, runs.Count);
        Assert.Equal(7m, runs[0].Report.NetPnL);
        Assert.Equal(5m, runs[1].Report.NetPnL);
        Assert.Equal(3m, runs[2].Report.NetPnL);
    }

    [Fact]
    public async Task RunAsync_Adaptive_RejectsZeroBudget()
    {
        var ranges = new[] { new ParameterRange("X", Min: 1m, Max: 5m, Step: 1m) };
        var fake = new FakeAdaptiveStrategy(suggestedXValues: Array.Empty<decimal>());
        var optimizer = new StrategyOptimizer(NullLogger<StrategyOptimizer>.Instance);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => optimizer.RunAsync(
            ranges, fake, budget: 0,
            runOne: (_, _) => Task.FromResult(MakeReport(0m))));
        Assert.Equal("budget", ex.ParamName);
    }

    [Fact]
    public async Task RunAsync_Adaptive_RunOneException_ReportsMinValueAndContinues()
    {
        var ranges = new[] { new ParameterRange("X", Min: 1m, Max: 10m, Step: 1m) };
        var fake = new FakeAdaptiveStrategy(suggestedXValues: new[] { 1m, 2m, 3m });
        var optimizer = new StrategyOptimizer(NullLogger<StrategyOptimizer>.Instance);

        var runs = await optimizer.RunAsync(
            ranges, fake, budget: 3,
            runOne: (paramSet, _) =>
            {
                if (paramSet["X"] == 2m) throw new InvalidOperationException("simulated runOne fault");
                return Task.FromResult(MakeReport(paramSet["X"]));
            });

        // budget=3，但中間一個失敗 → results 只有 2 筆 (X=1, X=3)
        Assert.Equal(2, runs.Count);

        // 失敗 trial 仍要回報 → ReportedValues 三筆，含 decimal.MinValue
        Assert.Equal(3, fake.ReportedValues.Count);
        Assert.Contains(decimal.MinValue, fake.ReportedValues);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task RunAsync_Adaptive_DisposesEvenWhenCancelled()
    {
        var ranges = new[] { new ParameterRange("X", Min: 1m, Max: 5m, Step: 1m) };
        var fake = new FakeAdaptiveStrategy(suggestedXValues: new[] { 1m, 2m, 3m });
        var optimizer = new StrategyOptimizer(NullLogger<StrategyOptimizer>.Instance);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => optimizer.RunAsync(
            ranges, fake, budget: 3,
            runOne: (paramSet, _) =>
            {
                cts.Cancel();
                return Task.FromResult(MakeReport(paramSet["X"]));
            },
            ct: cts.Token));

        Assert.True(fake.Disposed);
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

    private sealed class FakeAdaptiveStrategy : IAdaptiveSearchStrategy
    {
        private readonly Queue<decimal> _xs;

        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }
        public int SuggestCount { get; private set; }
        public List<decimal> ReportedValues { get; } = new();

        public FakeAdaptiveStrategy(IEnumerable<decimal> suggestedXValues)
        {
            _xs = new Queue<decimal>(suggestedXValues);
        }

        public Task InitializeAsync(IReadOnlyList<ParameterRange> ranges, int budget, CancellationToken ct)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, decimal>> SuggestNextAsync(CancellationToken ct)
        {
            SuggestCount++;
            var x = _xs.Dequeue();
            IReadOnlyDictionary<string, decimal> result = new Dictionary<string, decimal> { ["X"] = x };
            return Task.FromResult(result);
        }

        public Task ReportResultAsync(IReadOnlyDictionary<string, decimal> parameters, decimal value, CancellationToken ct)
        {
            ReportedValues.Add(value);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
