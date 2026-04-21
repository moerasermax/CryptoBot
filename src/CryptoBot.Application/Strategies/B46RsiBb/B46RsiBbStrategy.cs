using CryptoBot.Application.Indicators;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Strategies.B46RsiBb;

/// <summary>
/// B46 — RSI + Bollinger Band 均值回歸策略。
///
/// 預設參數：
/// - RsiPeriod = 14, RsiOversold = 30, RsiOverbought = 70
/// - BbPeriod  = 20, BbStdDev    = 2.0
///
/// 進場：
/// - Long：K 線收盤價 &lt; BB 下軌 且 RSI &lt; Oversold
/// - Short：K 線收盤價 &gt; BB 上軌 且 RSI &gt; Overbought
///
/// 平倉：
/// - 已持多單 → 收盤價回升至 BB 中軌（含以上）即 CloseLong
/// - 已持空單 → 收盤價回落至 BB 中軌（含以下）即 CloseShort
/// - 止損 / 止盈則由上層 RiskManager 根據 OpenLong/OpenShort 下的 SL/TP 價格處理，
///   此策略本身不負責維持止損狀態機。
///
/// 指標計算：使用 <see cref="TechnicalIndicators"/>（in-repo、decimal-strict，
/// 避免引入 Skender NuGet 造成精度下降 — 憲章 §2「decimal-only」）。
/// </summary>
public sealed class B46RsiBbStrategy : IStrategy
{
    public string StrategyType => "B46RsiBb";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        var rsiPeriod     = (int)config.GetParameter("RsiPeriod", 14);
        var rsiOversold   = config.GetParameter("RsiOversold", 30m);
        var rsiOverbought = config.GetParameter("RsiOverbought", 70m);
        var bbPeriod      = (int)config.GetParameter("BbPeriod", 20);
        var bbStdDev      = config.GetParameter("BbStdDev", 2m);

        var required = Math.Max(rsiPeriod + 1, bbPeriod);
        if (klines.Count < required)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var rsi = TechnicalIndicators.RSI(klines, rsiPeriod);
        var bb  = TechnicalIndicators.CalculateBollingerBands(klines, bbPeriod, bbStdDev);

        var last = klines.Count - 1;
        if (rsi[last] is null || bb.Upper[last] is null || bb.Middle[last] is null || bb.Lower[last] is null)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var close  = klines[last].Close;
        var rsiNow = rsi[last]!.Value;
        var upper  = bb.Upper[last]!.Value;
        var middle = bb.Middle[last]!.Value;
        var lower  = bb.Lower[last]!.Value;

        var currentPrice = snapshot.FuturesMarkPrice;

        // 已有持倉 → 看是否觸發中軌回歸平倉。
        var existing = openPositions.FirstOrDefault(p => !p.IsClosed);
        if (existing is not null)
        {
            if (existing.Side == PositionSide.Long && close >= middle)
                return Task.FromResult(TradingSignal.CloseLong(
                    config.Symbol, currentPrice,
                    $"B46 mean-revert: close({close:F2}) >= BB middle({middle:F2})"));

            if (existing.Side == PositionSide.Short && close <= middle)
                return Task.FromResult(TradingSignal.CloseShort(
                    config.Symbol, currentPrice,
                    $"B46 mean-revert: close({close:F2}) <= BB middle({middle:F2})"));

            return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
        }

        // 進場判斷
        if (close < lower && rsiNow < rsiOversold)
        {
            var sl = Price.Create(currentPrice.Value * (1 - config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 + config.TakeProfitPercent));
            return Task.FromResult(TradingSignal.OpenLong(
                config.Symbol, currentPrice, sl, tp,
                confidence: 0.7m,
                reason: $"B46 long: close({close:F2}) < BB lower({lower:F2}) & RSI({rsiNow:F1}) < {rsiOversold}"));
        }

        if (close > upper && rsiNow > rsiOverbought)
        {
            var sl = Price.Create(currentPrice.Value * (1 + config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 - config.TakeProfitPercent));
            return Task.FromResult(TradingSignal.OpenShort(
                config.Symbol, currentPrice, sl, tp,
                confidence: 0.7m,
                reason: $"B46 short: close({close:F2}) > BB upper({upper:F2}) & RSI({rsiNow:F1}) > {rsiOverbought}"));
        }

        return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
    }
}
