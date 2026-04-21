using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Application.Strategies;

/// <summary>
/// 由 <c>Strategy.StrategyType</c> 字串取得對應的 <see cref="IStrategy"/> 實作。
///
/// 這層存在是為了把「設定檔上的字串」與「具體策略程式類別」解耦 —
/// Strategy Aggregate 的 <c>StrategyType</c> 可以從 DB 讀出，不需直接引用類別型別。
/// </summary>
public interface IStrategyFactory
{
    /// <summary>依類型字串取得策略實作。找不到丟 <see cref="DomainException"/>。</summary>
    IStrategy Get(string strategyType);

    /// <summary>
    /// S25：所有已註冊的策略類型字串（Dashboard 下拉選單用）。
    /// 等同於 DI 容器裡所有具體 <see cref="IStrategy"/> 實作的 <c>StrategyType</c> 集合。
    /// </summary>
    IReadOnlyList<string> KnownTypes { get; }
}

/// <summary>
/// 預設實作：由 DI 注入所有 <see cref="IStrategy"/> 實例，建立 StrategyType → IStrategy 索引。
/// 策略實作必須是無狀態的（邏輯從 <c>StrategyConfiguration</c> 導出），不同策略實例共用同一個
/// <see cref="IStrategy"/> 物件 — 這也是為什麼 <see cref="IStrategy"/> 沒有持有配置，而是每次
/// <c>AnalyzeAsync</c> 都傳入。
/// </summary>
public sealed class StrategyFactory : IStrategyFactory
{
    private readonly IReadOnlyDictionary<string, IStrategy> _byType;

    public StrategyFactory(IEnumerable<IStrategy> strategies)
    {
        var map = new Dictionary<string, IStrategy>(StringComparer.Ordinal);
        foreach (var s in strategies)
        {
            if (map.ContainsKey(s.StrategyType))
                throw new DomainException(
                    $"Duplicate IStrategy registration for type '{s.StrategyType}'.");
            map[s.StrategyType] = s;
        }
        _byType = map;
    }

    public IStrategy Get(string strategyType)
    {
        if (_byType.TryGetValue(strategyType, out var impl)) return impl;
        throw new DomainException(
            $"No IStrategy registered for type '{strategyType}'. " +
            $"Known: [{string.Join(", ", _byType.Keys)}].");
    }

    public IReadOnlyList<string> KnownTypes => _byType.Keys.OrderBy(k => k).ToArray();
}
