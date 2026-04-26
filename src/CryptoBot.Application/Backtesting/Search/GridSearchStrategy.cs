namespace CryptoBot.Application.Backtesting.Search;

/// <summary>
/// 網格搜尋 — 把所有 <see cref="ParameterRange"/> 展開成笛卡兒積。
///
/// 行為與重構前 <see cref="StrategyOptimizer"/> 內嵌的 <c>CartesianProduct</c> 完全等價，
/// 包含相同的列舉順序：foreach 順序跟隨 <c>ranges</c> 的索引、每個維度內按 Min→Max 步進，
/// 因此既有「相同 ranges 跑兩次得到一致排行」的保證在重構後仍然成立。
/// </summary>
public sealed class GridSearchStrategy : ISearchStrategy
{
    public IEnumerable<IReadOnlyDictionary<string, decimal>> Enumerate(
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
}
