using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;

namespace CryptoBot.Application.Strategies;

/// <summary>
/// 交易策略介面 - Strategy Pattern
/// 
/// 每個具體策略 (TrendFollowing / MeanReversion / Arbitrage) 
/// 實作此介面以產生交易訊號。
/// </summary>
public interface IStrategy
{
    /// <summary>策略類型識別 (例: "TrendFollowing")</summary>
    string StrategyType { get; }

    /// <summary>
    /// 分析市場數據並產生交易訊號
    /// </summary>
    /// <param name="config">策略配置</param>
    /// <param name="klines">歷史 K 線 (由新到舊或舊到新?  本框架約定: 舊→新)</param>
    /// <param name="snapshot">當前市場快照</param>
    /// <param name="openPositions">該策略當前的持倉</param>
    /// <returns>交易訊號</returns>
    Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default);
}
