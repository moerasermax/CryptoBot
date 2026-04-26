namespace CryptoBot.Application.Backtesting.Search;

/// <summary>
/// 隨機搜尋 — 各維度獨立均勻取樣，跑 <paramref name="budget"/> 次（與網格大小無關）。
///
/// 抽樣空間：每個 <see cref="ParameterRange"/> 先以 <see cref="ParameterRange.Enumerate"/>
/// 物化成離散值池，再從池中均勻挑一個 — 因此即使是 <c>Step=0.5</c> 的 decimal 範圍，
/// 也只會抽到合法的步進值，不會出現步進外的浮點亂數，與 Grid 模式的可達點集合完全一致。
///
/// 可重現性：傳入 <c>seed</c> 即可固定序列（測試與回放需求）。不傳 seed 時用系統熵源。
/// 由於 <see cref="StrategyOptimizer.RunAsync"/> 會在主執行緒一次性 <c>ToList()</c>，
/// 內部 <see cref="Random"/> 不需要 thread-safe。
/// </summary>
public sealed class RandomSearchStrategy : ISearchStrategy
{
    private readonly int _budget;
    private readonly Random _random;

    public RandomSearchStrategy(int budget, int? seed = null)
    {
        if (budget <= 0)
            throw new ArgumentException($"Budget must be positive: {budget}", nameof(budget));

        _budget = budget;
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public IEnumerable<IReadOnlyDictionary<string, decimal>> Enumerate(
        IReadOnlyList<ParameterRange> ranges)
    {
        // 先把每個維度展平成陣列 — 同一個 budget 內反覆抽樣不需要重跑 Enumerate yield。
        var pools = ranges.Select(r => r.Enumerate().ToArray()).ToArray();

        for (var i = 0; i < _budget; i++)
        {
            var combo = new Dictionary<string, decimal>(ranges.Count);
            for (var d = 0; d < ranges.Count; d++)
            {
                var pool = pools[d];
                combo[ranges[d].Name] = pool[_random.Next(pool.Length)];
            }
            yield return combo;
        }
    }
}
