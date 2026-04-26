using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Backtesting.Search;
using Xunit;

namespace CryptoBot.Application.Tests.Backtesting.Search;

/// <summary>
/// S67 — GridSearchStrategy 行為驗證：
/// 確保重構後的笛卡兒展開與重構前 <c>StrategyOptimizer.CartesianProduct</c> 邏輯等價。
/// </summary>
public class GridSearchStrategyTests
{
    [Fact]
    public void Enumerate_SingleRange_YieldsOnePerStep()
    {
        var ranges = new[]
        {
            new ParameterRange("X", Min: 1m, Max: 3m, Step: 1m),
        };
        var combos = new GridSearchStrategy().Enumerate(ranges).ToList();

        Assert.Equal(3, combos.Count);
        Assert.Equal(new[] { 1m, 2m, 3m }, combos.Select(c => c["X"]).ToArray());
    }

    [Fact]
    public void Enumerate_TwoRanges_YieldsCartesianProduct()
    {
        var ranges = new[]
        {
            new ParameterRange("A", Min: 1m, Max: 2m, Step: 1m),  // {1, 2}
            new ParameterRange("B", Min: 10m, Max: 30m, Step: 10m), // {10, 20, 30}
        };
        var combos = new GridSearchStrategy().Enumerate(ranges).ToList();

        // 笛卡兒積 = 2 * 3 = 6 組合
        Assert.Equal(6, combos.Count);

        // 確保所有期望組合都出現過
        Assert.Contains(combos, c => c["A"] == 1m && c["B"] == 10m);
        Assert.Contains(combos, c => c["A"] == 1m && c["B"] == 20m);
        Assert.Contains(combos, c => c["A"] == 1m && c["B"] == 30m);
        Assert.Contains(combos, c => c["A"] == 2m && c["B"] == 10m);
        Assert.Contains(combos, c => c["A"] == 2m && c["B"] == 20m);
        Assert.Contains(combos, c => c["A"] == 2m && c["B"] == 30m);
    }

    [Fact]
    public void Enumerate_DeterministicOrder_AcrossInvocations()
    {
        // 同樣的 ranges 連跑兩次必須吐出完全一致的組合序列 — 否則優化結果不可重現。
        var ranges = new[]
        {
            new ParameterRange("Fast", Min: 5m, Max: 15m, Step: 5m),
            new ParameterRange("Slow", Min: 20m, Max: 40m, Step: 10m),
        };
        var first  = new GridSearchStrategy().Enumerate(ranges).ToList();
        var second = new GridSearchStrategy().Enumerate(ranges).ToList();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i]["Fast"], second[i]["Fast"]);
            Assert.Equal(first[i]["Slow"], second[i]["Slow"]);
        }
    }
}
