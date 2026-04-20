using CryptoBot.Application.Indicators;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Strategies.MeanReversion;

/// <summary>
/// 均值回歸策略 - RSI 超買超賣 + Bollinger Bands 價格回歸中軸
/// 
/// 進場條件 (做多):
/// - 價格觸及或跌破布林下軌
/// - RSI < 30 (超賣)
/// 
/// 進場條件 (做空):
/// - 價格觸及或突破布林上軌
/// - RSI > 70 (超買)
/// 
/// 出場條件:
/// - 價格回到布林中軌 (中線均線)
/// - 或止損/止盈觸發
/// </summary>
public sealed class MeanReversionStrategy : IStrategy
{
    public string StrategyType => "MeanReversion";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        var bbPeriod = (int)config.GetParameter("BollingerPeriod", 20);
        var bbStdDev = config.GetParameter("BollingerStdDev", 2);
        var rsiPeriod = (int)config.GetParameter("RsiPeriod", 14);
        var oversoldLevel = config.GetParameter("RsiOversold", 30);
        var overboughtLevel = config.GetParameter("RsiOverbought", 70);

        if (klines.Count < bbPeriod + 5)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var bb = TechnicalIndicators.CalculateBollingerBands(klines, bbPeriod, bbStdDev);
        var rsi = TechnicalIndicators.RSI(klines, rsiPeriod);
        var last = klines.Count - 1;

        if (bb.Upper[last] is null || bb.Lower[last] is null
            || bb.Middle[last] is null || rsi[last] is null)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var currentPrice = snapshot.FuturesMarkPrice;
        var upper = bb.Upper[last]!.Value;
        var middle = bb.Middle[last]!.Value;
        var lower = bb.Lower[last]!.Value;
        var currentRsi = rsi[last]!.Value;

        // === 已有持倉 - 檢查平倉 ===
        var existingPosition = openPositions.FirstOrDefault(p => !p.IsClosed);
        if (existingPosition is not null)
        {
            if (existingPosition.Side == PositionSide.Long
                && currentPrice.Value >= middle)
            {
                return Task.FromResult(TradingSignal.CloseLong(
                    config.Symbol, currentPrice,
                    $"Price reverted to BB middle ({middle:F2}), exit long"));
            }
            if (existingPosition.Side == PositionSide.Short
                && currentPrice.Value <= middle)
            {
                return Task.FromResult(TradingSignal.CloseShort(
                    config.Symbol, currentPrice,
                    $"Price reverted to BB middle ({middle:F2}), exit short"));
            }
            return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
        }

        // === 無持倉 - 檢查進場 ===

        // 做多: 觸及下軌 + RSI 超賣
        if (currentPrice.Value <= lower && currentRsi < oversoldLevel)
        {
            var sl = Price.Create(currentPrice.Value * (1 - config.StopLossPercent));
            // 均值回歸的止盈目標通常是中軌
            var tpTarget = Math.Min(
                middle,
                currentPrice.Value * (1 + config.TakeProfitPercent));
            var tp = Price.Create(tpTarget);

            // RSI 越低信心越強
            var confidence = 0.5m + Math.Min(0.4m, (oversoldLevel - currentRsi) / 100m);

            return Task.FromResult(TradingSignal.OpenLong(
                config.Symbol, currentPrice, sl, tp, confidence,
                $"MR Long: price={currentPrice} <= BB Lower={lower:F2}, RSI={currentRsi:F1}"));
        }

        // 做空: 觸及上軌 + RSI 超買
        if (currentPrice.Value >= upper && currentRsi > overboughtLevel)
        {
            var sl = Price.Create(currentPrice.Value * (1 + config.StopLossPercent));
            var tpTarget = Math.Max(
                middle,
                currentPrice.Value * (1 - config.TakeProfitPercent));
            var tp = Price.Create(tpTarget);

            var confidence = 0.5m + Math.Min(0.4m, (currentRsi - overboughtLevel) / 100m);

            return Task.FromResult(TradingSignal.OpenShort(
                config.Symbol, currentPrice, sl, tp, confidence,
                $"MR Short: price={currentPrice} >= BB Upper={upper:F2}, RSI={currentRsi:F1}"));
        }

        return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
    }
}
