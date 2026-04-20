using CryptoBot.Application.Indicators;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Strategies.SmaCrossover;

/// <summary>
/// 經典 SMA 快慢均線交叉策略（全線試車用）。
///
/// 預設參數：
/// - FastSmaPeriod = 20
/// - SlowSmaPeriod = 50
///
/// 進場邏輯：
/// - Golden Cross（fast 由下穿上 slow）→ <see cref="SignalType.OpenLong"/>
/// - Death Cross（fast 由上穿下 slow）→ <see cref="SignalType.OpenShort"/>
///
/// 平倉邏輯：
/// - 持有多單時遇到 Death Cross → <see cref="SignalType.CloseLong"/>
/// - 持有空單時遇到 Golden Cross → <see cref="SignalType.CloseShort"/>
///
/// 與 <see cref="TrendFollowing.TrendFollowingStrategy"/> 的差異：
/// TrendFollowing 使用 EMA + RSI 濾雜訊；此策略刻意保持單純 SMA 交叉，
/// 作為 S7 全線串通的基準訊號源。
/// </summary>
public sealed class SmaCrossoverStrategy : IStrategy
{
    public string StrategyType => "SmaCrossover";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        var fastPeriod = (int)config.GetParameter("FastSmaPeriod", 20);
        var slowPeriod = (int)config.GetParameter("SlowSmaPeriod", 50);

        if (klines.Count < slowPeriod + 1)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var smaFast = TechnicalIndicators.SMA(klines, fastPeriod);
        var smaSlow = TechnicalIndicators.SMA(klines, slowPeriod);

        var last = klines.Count - 1;
        var prev = last - 1;

        if (smaFast[last] is null || smaSlow[last] is null
            || smaFast[prev] is null || smaSlow[prev] is null)
            return Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

        var fastPrev = smaFast[prev]!.Value;
        var slowPrev = smaSlow[prev]!.Value;
        var fastNow  = smaFast[last]!.Value;
        var slowNow  = smaSlow[last]!.Value;

        bool goldenCross = fastPrev <= slowPrev && fastNow > slowNow;
        bool deathCross  = fastPrev >= slowPrev && fastNow < slowNow;

        var currentPrice = snapshot.FuturesMarkPrice;

        // 已有持倉 → 反向交叉才考慮平倉
        var existing = openPositions.FirstOrDefault(p => !p.IsClosed);
        if (existing is not null)
        {
            if (existing.Side == PositionSide.Long && deathCross)
                return Task.FromResult(TradingSignal.CloseLong(
                    config.Symbol, currentPrice, "SMA death cross — exit long"));

            if (existing.Side == PositionSide.Short && goldenCross)
                return Task.FromResult(TradingSignal.CloseShort(
                    config.Symbol, currentPrice, "SMA golden cross — exit short"));

            return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
        }

        if (goldenCross)
        {
            var sl = Price.Create(currentPrice.Value * (1 - config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 + config.TakeProfitPercent));
            return Task.FromResult(TradingSignal.OpenLong(
                config.Symbol, currentPrice, sl, tp,
                confidence: 0.7m,
                reason: $"SMA golden cross: fast(SMA{fastPeriod})={fastNow:F2} slow(SMA{slowPeriod})={slowNow:F2}"));
        }

        if (deathCross)
        {
            var sl = Price.Create(currentPrice.Value * (1 + config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 - config.TakeProfitPercent));
            return Task.FromResult(TradingSignal.OpenShort(
                config.Symbol, currentPrice, sl, tp,
                confidence: 0.7m,
                reason: $"SMA death cross: fast(SMA{fastPeriod})={fastNow:F2} slow(SMA{slowPeriod})={slowNow:F2}"));
        }

        return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
    }
}
