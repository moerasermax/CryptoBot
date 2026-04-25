using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;

namespace CryptoBot.Application.Strategies;

/// <summary>
/// S63 Phase 3：多週期策略介面。實作此介面的策略會告知引擎自己需要哪幾個額外週期的 K 線；
/// <see cref="StrategyExecutor"/> 會在每次主週期 K 線觸發時並行抓取額外週期，並把「已切除進行中尾根」
/// 的資料交給策略。
///
/// 設計原則：
///   - 繼承 <see cref="IStrategy"/>：向下相容，所有註冊為 <c>IStrategy</c> 的地方都能找到它。
///   - 主週期仍然沿 <see cref="StrategyConfiguration.Interval"/>；<see cref="RequiredIntervals"/>
///     只宣告「除了主週期之外**另外**需要的」週期（若不小心包含主週期，執行器會自動去重）。
///   - 既有的 <see cref="IStrategy.AnalyzeAsync"/> 不會被執行器呼叫到本介面實作 —
///     但為相容（例如回測器、單元測試直接 call）仍保留其語意，建議實作回傳 None 或轉呼叫
///     <see cref="AnalyzeMultiTimeframeAsync"/> 並傳入空的 additionalKlines。
/// </summary>
public interface IMultiTimeframeStrategy : IStrategy
{
    /// <summary>
    /// 除主週期之外還需要的 K 線週期。空集合 = 退化為一般策略。
    /// 執行器會自動去重、剔除與主週期相同的條目。
    /// </summary>
    IReadOnlyList<KlineInterval> RequiredIntervals { get; }

    /// <summary>
    /// 多週期版本的分析入口。
    /// </summary>
    /// <param name="config">策略配置（主週期 = <see cref="StrategyConfiguration.Interval"/>）。</param>
    /// <param name="primaryKlines">主週期 K 線（舊→新），可能包含「進行中」尾根（與舊行為一致）。</param>
    /// <param name="additionalKlines">
    /// 額外週期 K 線字典。執行器已把每個週期的「進行中」尾根切除，
    /// 所以 last 永遠是已收盤 K 線 — 策略可以安心把它當成信號來源。
    /// Key 只包含 <see cref="RequiredIntervals"/> 中實際成功抓到的週期；若某週期 REST 失敗，
    /// 該 key 不存在（策略應優雅降級）。
    /// </param>
    Task<TradingSignal> AnalyzeMultiTimeframeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> primaryKlines,
        IReadOnlyDictionary<KlineInterval, IReadOnlyList<Kline>> additionalKlines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default);
}
