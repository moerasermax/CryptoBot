using CryptoBot.Domain.Common;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Aggregates.StrategyAggregate;

/// <summary>
/// 策略配置 - Value Object
/// </summary>
public sealed class StrategyConfiguration : ValueObject
{
    public Symbol Symbol { get; }
    public KlineInterval Interval { get; }
    public Leverage Leverage { get; }
    public decimal RiskPerTradePercent { get; }    // 單筆風險佔總資金 %
    public decimal StopLossPercent { get; }         // 止損距離 %
    public decimal TakeProfitPercent { get; }       // 止盈距離 %
    public decimal? TrailingStopPercent { get; }    // 追蹤停損 (選用)
    public int MaxConcurrentPositions { get; }      // 最大同時持倉數

    /// <summary>下單冷卻時間 — 同策略連續下單的最小間隔。預設 30 秒。</summary>
    public TimeSpan CooldownPeriod { get; }

    /// <summary>策略可見的滾動 K 線視窗大小（用於指標計算）。預設 200 根。</summary>
    public int MaxKlineWindow { get; }

    public IReadOnlyDictionary<string, decimal> Parameters { get; }

    private StrategyConfiguration(
        Symbol symbol, KlineInterval interval, Leverage leverage,
        decimal riskPerTradePercent, decimal stopLossPercent, decimal takeProfitPercent,
        decimal? trailingStopPercent, int maxConcurrentPositions,
        TimeSpan cooldownPeriod, int maxKlineWindow,
        IReadOnlyDictionary<string, decimal> parameters)
    {
        Symbol = symbol;
        Interval = interval;
        Leverage = leverage;
        RiskPerTradePercent = riskPerTradePercent;
        StopLossPercent = stopLossPercent;
        TakeProfitPercent = takeProfitPercent;
        TrailingStopPercent = trailingStopPercent;
        MaxConcurrentPositions = maxConcurrentPositions;
        CooldownPeriod = cooldownPeriod;
        MaxKlineWindow = maxKlineWindow;
        Parameters = parameters;
    }

    public static StrategyConfiguration Create(
        Symbol symbol,
        KlineInterval interval,
        Leverage leverage,
        decimal riskPerTradePercent = 0.02m,   // 預設 2%
        decimal stopLossPercent = 0.02m,        // 預設 2%
        decimal takeProfitPercent = 0.04m,      // 預設 4% (風報比 2:1)
        decimal? trailingStopPercent = null,
        int maxConcurrentPositions = 1,
        TimeSpan? cooldownPeriod = null,
        int maxKlineWindow = 200,
        IReadOnlyDictionary<string, decimal>? parameters = null)
    {
        if (riskPerTradePercent <= 0 || riskPerTradePercent > 0.1m)
            throw new DomainException(
                $"Risk per trade must be in (0, 10%]: got {riskPerTradePercent:P}");
        if (stopLossPercent <= 0 || stopLossPercent > 0.5m)
            throw new DomainException(
                $"Stop loss must be in (0, 50%]: got {stopLossPercent:P}");
        if (takeProfitPercent <= 0)
            throw new DomainException("Take profit must be positive.");
        if (maxConcurrentPositions < 1)
            throw new DomainException("Max concurrent positions must be >= 1.");
        if (maxKlineWindow < 1)
            throw new DomainException(
                $"Max kline window must be >= 1: got {maxKlineWindow}");

        var effectiveCooldown = cooldownPeriod ?? TimeSpan.FromSeconds(30);
        if (effectiveCooldown < TimeSpan.Zero)
            throw new DomainException(
                $"Cooldown period cannot be negative: {effectiveCooldown}");

        return new StrategyConfiguration(
            symbol, interval, leverage,
            riskPerTradePercent, stopLossPercent, takeProfitPercent,
            trailingStopPercent, maxConcurrentPositions,
            effectiveCooldown, maxKlineWindow,
            parameters ?? new Dictionary<string, decimal>());
    }

    public decimal GetParameter(string key, decimal defaultValue = 0) =>
        Parameters.TryGetValue(key, out var v) ? v : defaultValue;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Symbol;
        yield return Interval;
        yield return Leverage;
        yield return RiskPerTradePercent;
        yield return StopLossPercent;
        yield return TakeProfitPercent;
        yield return CooldownPeriod;
        yield return MaxKlineWindow;
    }
}
