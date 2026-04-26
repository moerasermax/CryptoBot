using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Backtesting.Search;
using Xunit;

namespace CryptoBot.Application.Tests.Backtesting.Search;

/// <summary>
/// S67 — RandomSearchStrategy 行為驗證：
/// 1) Budget 控制取樣次數，2) 抽出來的值必落在 ParameterRange.Enumerate 的離散集合內，
/// 3) 種子固定即 deterministic（測試與回放需求），4) 非法 budget 直接拒絕。
/// </summary>
public class RandomSearchStrategyTests
{
    [Fact]
    public void Enumerate_BudgetEqualsCount()
    {
        var ranges = new[]
        {
            new ParameterRange("A", Min: 1m, Max: 10m, Step: 1m),
            new ParameterRange("B", Min: 100m, Max: 200m, Step: 10m),
        };
        var combos = new RandomSearchStrategy(budget: 25, seed: 42).Enumerate(ranges).ToList();

        Assert.Equal(25, combos.Count);
    }

    [Fact]
    public void Enumerate_AllSamplesLandOnLegalGridPoints()
    {
        // 抽出來的值必須落在 ParameterRange.Enumerate 的合法步進集合上 —
        // 否則 Grid 與 Random 的可達點集合會分歧（步進值外的浮點雜訊會出現）。
        var aRange = new ParameterRange("A", Min: 1m, Max: 5m, Step: 1m);
        var bRange = new ParameterRange("B", Min: 0.5m, Max: 2.5m, Step: 0.5m);
        var aLegal = aRange.Enumerate().ToHashSet();
        var bLegal = bRange.Enumerate().ToHashSet();

        var combos = new RandomSearchStrategy(budget: 100, seed: 7).Enumerate(new[] { aRange, bRange }).ToList();

        foreach (var c in combos)
        {
            Assert.Contains(c["A"], aLegal);
            Assert.Contains(c["B"], bLegal);
        }
    }

    [Fact]
    public void Enumerate_SameSeedYieldsIdenticalSequence()
    {
        // deterministic：相同 seed 跑兩次必須得出完全一致的序列，回放才可重現。
        var ranges = new[]
        {
            new ParameterRange("A", Min: 1m, Max: 10m, Step: 1m),
            new ParameterRange("B", Min: 100m, Max: 500m, Step: 50m),
        };

        var first  = new RandomSearchStrategy(budget: 30, seed: 12345).Enumerate(ranges).ToList();
        var second = new RandomSearchStrategy(budget: 30, seed: 12345).Enumerate(ranges).ToList();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i]["A"], second[i]["A"]);
            Assert.Equal(first[i]["B"], second[i]["B"]);
        }
    }

    [Fact]
    public void Enumerate_DifferentSeedYieldsDifferentSequence()
    {
        // sanity check — 種子改變應該至少有一筆不同（避免 Random.Next 被無腦寫成常數）。
        var ranges = new[]
        {
            new ParameterRange("A", Min: 1m, Max: 100m, Step: 1m),
        };

        var seedA = new RandomSearchStrategy(budget: 50, seed: 1).Enumerate(ranges).ToList();
        var seedB = new RandomSearchStrategy(budget: 50, seed: 2).Enumerate(ranges).ToList();

        var anyDifferent = seedA.Zip(seedB, (a, b) => a["A"] != b["A"]).Any(x => x);
        Assert.True(anyDifferent, "兩個不同種子產生完全相同的序列 — 隨機性可能被踢掉了。");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_RejectsNonPositiveBudget(int budget)
    {
        var ex = Assert.Throws<ArgumentException>(() => new RandomSearchStrategy(budget));
        Assert.Equal("budget", ex.ParamName);
    }
}
