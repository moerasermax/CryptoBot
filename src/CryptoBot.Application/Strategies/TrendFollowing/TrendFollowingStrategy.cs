using CryptoBot.Application.Indicators;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Strategies.TrendFollowing;

/// <summary>
/// 趨勢跟蹤策略 - EMA 黃金交叉/死亡交叉 + RSI 確認
/// 
/// 進場條件 (做多):
/// - 快速 EMA (12) 上穿慢速 EMA (26)
/// - RSI > 50 (動能確認)
/// - 價格在慢速 EMA 之上 (確認上升趨勢)
/// 
/// 進場條件 (做空):
/// - 快速 EMA 下穿慢速 EMA
/// - RSI < 50
/// - 價格在慢速 EMA 之下
/// 
/// 出場條件:
/// - 反向交叉 或 止損/止盈觸發
/// </summary>
public sealed class TrendFollowingStrategy : IStrategy
{
    public string StrategyType => "TrendFollowing";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        var fastPeriod = (int)config.GetParameter("FastEmaPeriod", 12);
        var slowPeriod = (int)config.GetParameter("SlowEmaPeriod", 26);
        var rsiPeriod = (int)config.GetParameter("RsiPeriod", 14);
        var rsiMidline = config.GetParameter("RsiMidline", 50);

        // 數據不足
        if (klines.Count < slowPeriod + 5)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var emaFast = TechnicalIndicators.EMA(klines, fastPeriod);
        var emaSlow = TechnicalIndicators.EMA(klines, slowPeriod);
        var rsi = TechnicalIndicators.RSI(klines, rsiPeriod);

        var last = klines.Count - 1;
        var prev = last - 1;

        // 若任何指標值為 null, 不產生訊號
        if (emaFast[last] is null || emaSlow[last] is null
            || emaFast[prev] is null || emaSlow[prev] is null
            || rsi[last] is null)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var currentPrice = snapshot.FuturesMarkPrice;

        // === 若已有持倉 → 檢查是否需要平倉 ===
        var existingPosition = openPositions.FirstOrDefault(p => !p.IsClosed);
        if (existingPosition is not null)
        {
            return Task.FromResult(CheckExitSignal(
                existingPosition, emaFast, emaSlow, last, prev, currentPrice, config.Symbol));
        }

        // === 無持倉 → 檢查進場 ===

        // 黃金交叉: 快線從下方穿越慢線
        bool goldenCross = emaFast[prev]!.Value <= emaSlow[prev]!.Value
                        && emaFast[last]!.Value > emaSlow[last]!.Value;

        // 死亡交叉: 快線從上方穿越慢線
        bool deathCross = emaFast[prev]!.Value >= emaSlow[prev]!.Value
                       && emaFast[last]!.Value < emaSlow[last]!.Value;

        if (goldenCross && rsi[last]!.Value > rsiMidline
            && currentPrice.Value > emaSlow[last]!.Value)
        {
            var sl = Price.Create(currentPrice.Value * (1 - config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 + config.TakeProfitPercent));

            // 信心度根據交叉強度
            var crossStrength = Math.Min(1m,
                Math.Abs(emaFast[last]!.Value - emaSlow[last]!.Value)
                / emaSlow[last]!.Value * 100m);
            var confidence = 0.5m + Math.Min(0.4m, crossStrength);

            return Task.FromResult(TradingSignal.OpenLong(
                config.Symbol, currentPrice, sl, tp, confidence,
                $"EMA golden cross: fast={emaFast[last]:F2} slow={emaSlow[last]:F2} RSI={rsi[last]:F1}"));
        }

        if (deathCross && rsi[last]!.Value < rsiMidline
            && currentPrice.Value < emaSlow[last]!.Value)
        {
            var sl = Price.Create(currentPrice.Value * (1 + config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 - config.TakeProfitPercent));
            var confidence = 0.6m;

            return Task.FromResult(TradingSignal.OpenShort(
                config.Symbol, currentPrice, sl, tp, confidence,
                $"EMA death cross: fast={emaFast[last]:F2} slow={emaSlow[last]:F2} RSI={rsi[last]:F1}"));
        }

        return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
    }

    private static TradingSignal CheckExitSignal(
        Position position,
        IReadOnlyList<decimal?> emaFast, IReadOnlyList<decimal?> emaSlow,
        int last, int prev, Price currentPrice, Symbol symbol)
    {
        // 反向交叉平倉
        if (position.Side == PositionSide.Long)
        {
            bool deathCross = emaFast[prev]!.Value >= emaSlow[prev]!.Value
                           && emaFast[last]!.Value < emaSlow[last]!.Value;
            if (deathCross)
                return TradingSignal.CloseLong(symbol, currentPrice,
                    "EMA death cross - exit long");
        }
        else  // Short
        {
            bool goldenCross = emaFast[prev]!.Value <= emaSlow[prev]!.Value
                            && emaFast[last]!.Value > emaSlow[last]!.Value;
            if (goldenCross)
                return TradingSignal.CloseShort(symbol, currentPrice,
                    "EMA golden cross - exit short");
        }
        return TradingSignal.None(symbol, currentPrice);
    }
}
