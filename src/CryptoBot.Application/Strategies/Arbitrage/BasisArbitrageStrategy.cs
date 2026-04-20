using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Strategies.Arbitrage;

/// <summary>
/// 現貨-永續合約套利策略 (Basis Trading)
/// 
/// 原理:
/// - 當永續合約溢價 (合約價 > 現貨價) 時, 做多現貨 + 做空合約, 等待基差收斂
/// - 當永續合約折價 (合約價 < 現貨價) 時, 做空受限不可直接做, 只能靠資金費率或平倉套利
/// 
/// 此策略在本框架中負責的是合約端的操作, 現貨端需要另行管理
/// 
/// 進場條件 (做空合約):
/// - 基差百分比 > 觸發閾值 (例: 0.3%)
/// - 資金費率為正 (收取資金費用的一方)
/// 
/// 出場條件:
/// - 基差收斂至接近 0
/// - 或基差擴大超過停損閾值
/// 
/// 注意: 真正的對沖套利需要同時操作現貨, 這裡簡化只示範合約端邏輯
/// </summary>
public sealed class BasisArbitrageStrategy : IStrategy
{
    public string StrategyType => "BasisArbitrage";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        // 套利需要現貨價格
        if (snapshot.SpotPrice is null || snapshot.BasisPercent is null)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var entryBasisThreshold = config.GetParameter("EntryBasisPercent", 0.3m);
        var exitBasisThreshold = config.GetParameter("ExitBasisPercent", 0.05m);
        var stopLossBasisThreshold = config.GetParameter("StopLossBasisPercent", 1.0m);

        var basisPercent = snapshot.BasisPercent.Value;
        var currentPrice = snapshot.FuturesMarkPrice;

        // === 已有持倉 - 檢查平倉 ===
        var existingPosition = openPositions.FirstOrDefault(p => !p.IsClosed);
        if (existingPosition is not null)
        {
            // 做空合約的套利持倉
            if (existingPosition.Side == PositionSide.Short)
            {
                // 基差收斂 → 獲利平倉
                if (Math.Abs(basisPercent) <= exitBasisThreshold)
                {
                    return Task.FromResult(TradingSignal.CloseShort(
                        config.Symbol, currentPrice,
                        $"Basis converged to {basisPercent:F3}%, close arbitrage short"));
                }
                // 基差擴大到風險閾值 → 停損
                if (basisPercent > stopLossBasisThreshold)
                {
                    return Task.FromResult(TradingSignal.CloseShort(
                        config.Symbol, currentPrice,
                        $"Basis widened to {basisPercent:F3}%, stop loss"));
                }
            }
            return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
        }

        // === 無持倉 - 檢查進場 ===

        // 合約溢價 - 做空合約 (預期價格收斂)
        if (basisPercent > entryBasisThreshold)
        {
            // 對套利而言, stop loss 不是價格而是基差
            // 這裡仍設一個價格停損作為安全網
            var sl = Price.Create(currentPrice.Value * 1.02m);  // 2% 硬停損
            var tp = Price.Create(snapshot.SpotPrice.Value);   // 預期收斂到現貨價
            var confidence = Math.Min(1m, basisPercent / (entryBasisThreshold * 2));

            return Task.FromResult(TradingSignal.OpenShort(
                config.Symbol, currentPrice, sl, tp, confidence,
                $"Arbitrage: Futures premium {basisPercent:F3}% > {entryBasisThreshold}% " +
                $"(Spot={snapshot.SpotPrice}, Futures={currentPrice}, " +
                $"Funding={snapshot.FundingRate:P4})"));
        }

        return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
    }
}
