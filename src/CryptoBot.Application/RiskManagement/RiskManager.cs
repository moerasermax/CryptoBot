using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.RiskManagement;

/// <summary>
/// 風險檢查結果
/// </summary>
public sealed record RiskCheckResult(bool IsApproved, string? Reason = null)
{
    public static RiskCheckResult Approved() => new(true);
    public static RiskCheckResult Rejected(string reason) => new(false, reason);
}

/// <summary>
/// 風險管理服務 - 多層安全網
/// 
/// 每次開倉前的檢查：
/// 1. 帳戶餘額 >= 所需保證金 + 保留資金
/// 2. 當前總名義敞口 < 最大敞口限制
/// 3. 單日已實現虧損 < 最大日虧損限制 (防止連續失敗)
/// 4. 同一交易對不可反向開倉 (除非啟用 hedge mode)
/// 5. 策略的最大持倉數未達上限
/// </summary>
public interface IRiskManager
{
    Task<RiskCheckResult> CheckBeforeOpenAsync(
        Strategy strategy,
        TradingSignal signal,
        Quantity plannedQuantity,
        CancellationToken ct = default);

    Task<bool> IsDailyLossLimitReachedAsync(CancellationToken ct = default);
    Task<decimal> GetTotalExposureAsync(CancellationToken ct = default);
}

public sealed class RiskManager : IRiskManager
{
    /// <summary>S66-D：時鐘漂移攔截閾值。超過此值即拒下單，防止 BingX 簽章因時間不對齊而失敗。</summary>
    internal const double ClockSkewRejectThresholdMs = 1000d;

    private readonly IExchangeClient _exchange;
    private readonly IPositionRepository _positionRepository;
    private readonly IStrategyCooldownTracker _cooldownTracker;
    private readonly RiskLimits _limits;
    private readonly IClockSkewState? _skewState;

    public RiskManager(
        IExchangeClient exchange,
        IPositionRepository positionRepository,
        IStrategyCooldownTracker cooldownTracker,
        RiskLimits limits,
        IClockSkewState? skewState = null)
    {
        _exchange = exchange;
        _positionRepository = positionRepository;
        _cooldownTracker = cooldownTracker;
        _limits = limits;
        _skewState = skewState;
    }

    public async Task<RiskCheckResult> CheckBeforeOpenAsync(
        Strategy strategy,
        TradingSignal signal,
        Quantity plannedQuantity,
        CancellationToken ct = default)
    {
        // S66-D：時鐘漂移檢查 — 比 cooldown 更早做（最便宜，純記憶體讀）。
        // 若已同步過至少一次且偏差 > 1000ms 則攔截，防止 BingX 簽章因時間不對齊而失敗。
        // 「未同步過」的早期啟動窗口不擋（不該因 NTP 監控還沒跑就誤殺下單）。
        if (_skewState is not null && _skewState.IsSynced)
        {
            var skewMs = _skewState.CurrentOffset.TotalMilliseconds;
            if (Math.Abs(skewMs) > ClockSkewRejectThresholdMs)
                return RiskCheckResult.Rejected(
                    $"NTP Drift detected (Skew: {(long)skewMs}ms). Order rejected for safety.");
        }

        // 0. 冷卻時間 — 最便宜的檢查，先做。
        if (_cooldownTracker.IsInCooldown(strategy.Id, strategy.Configuration.CooldownPeriod))
            return RiskCheckResult.Rejected(
                $"Strategy {strategy.Name} is in cooldown ({strategy.Configuration.CooldownPeriod.TotalSeconds:F0}s)");

        // 1. 策略必須在執行中
        if (strategy.Status != StrategyStatus.Running)
            return RiskCheckResult.Rejected($"Strategy status is {strategy.Status}");

        // 2. 檢查最大同時持倉數
        var strategyPositions = await _positionRepository.GetByStrategyIdAsync(
            strategy.Id, includeClosedPositions: false, ct);
        if (strategyPositions.Count >= strategy.Configuration.MaxConcurrentPositions)
            return RiskCheckResult.Rejected(
                $"Strategy {strategy.Name} already has " +
                $"{strategyPositions.Count} open positions " +
                $"(max {strategy.Configuration.MaxConcurrentPositions})");

        // 3. 檢查是否有反向持倉
        var openOnSymbol = await _positionRepository.GetOpenPositionsBySymbolAsync(
            signal.Symbol, ct);
        var opposingSide = signal.Type == SignalType.OpenLong
            ? PositionSide.Short : PositionSide.Long;
        if (openOnSymbol.Any(p => p.Side == opposingSide))
            return RiskCheckResult.Rejected(
                $"Already have opposing position on {signal.Symbol}");

        // 4. 帳戶餘額檢查
        var balance = await _exchange.GetFuturesBalanceAsync(ct: ct);
        var notionalValue = signal.SuggestedPrice.Value * plannedQuantity.Value;
        var requiredMargin = notionalValue / strategy.Configuration.Leverage.Value;
        var reservedBalance = balance * _limits.ReserveRatio;
        var available = balance - reservedBalance;

        if (requiredMargin > available)
            return RiskCheckResult.Rejected(
                $"Insufficient balance. Required: {requiredMargin:F4}, " +
                $"Available: {available:F4} (after {_limits.ReserveRatio:P0} reserve)");

        // 5. 總敞口檢查
        var totalExposure = await GetTotalExposureAsync(ct);
        var newExposure = totalExposure + notionalValue;
        var maxExposure = balance * _limits.MaxExposureMultiplier;
        if (newExposure > maxExposure)
            return RiskCheckResult.Rejected(
                $"Total exposure would exceed limit. " +
                $"Current: {totalExposure:F2}, New: {newExposure:F2}, Max: {maxExposure:F2}");

        // 6. 日虧損檢查
        if (await IsDailyLossLimitReachedAsync(ct))
            return RiskCheckResult.Rejected(
                $"Daily loss limit reached. Trading paused for safety.");

        return RiskCheckResult.Approved();
    }

    public async Task<bool> IsDailyLossLimitReachedAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);

        var closedToday = await _positionRepository.GetClosedPositionsInRangeAsync(
            today, tomorrow, ct);
        var totalLoss = closedToday.Where(p => p.RealizedPnL < 0).Sum(p => p.RealizedPnL);

        var balance = await _exchange.GetFuturesBalanceAsync(ct: ct);
        var lossLimit = balance * _limits.MaxDailyLossPercent;

        return Math.Abs(totalLoss) >= lossLimit;
    }

    public async Task<decimal> GetTotalExposureAsync(CancellationToken ct = default)
    {
        var positions = await _positionRepository.GetOpenPositionsAsync(ct);
        return positions.Sum(p => p.NotionalValue);
    }
}

/// <summary>
/// 全域風險限制配置
/// </summary>
public sealed record RiskLimits
{
    /// <summary>最大日虧損佔總資金比例 (例: 0.1 = 10%)</summary>
    public decimal MaxDailyLossPercent { get; init; } = 0.1m;

    /// <summary>最大總敞口倍數 (例: 3x 表示總名義價值 <= 本金 * 3)</summary>
    public decimal MaxExposureMultiplier { get; init; } = 3m;

    /// <summary>帳戶保留比例 (不得使用的資金)</summary>
    public decimal ReserveRatio { get; init; } = 0.1m;

    public static RiskLimits Conservative => new()
    {
        MaxDailyLossPercent = 0.05m,
        MaxExposureMultiplier = 2m,
        ReserveRatio = 0.2m
    };

    public static RiskLimits Moderate => new();

    public static RiskLimits Aggressive => new()
    {
        MaxDailyLossPercent = 0.15m,
        MaxExposureMultiplier = 5m,
        ReserveRatio = 0.05m
    };
}
