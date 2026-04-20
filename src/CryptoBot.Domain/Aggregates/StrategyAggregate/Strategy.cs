using CryptoBot.Domain.Common;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Events;
using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.Aggregates.StrategyAggregate;

/// <summary>
/// 策略 Aggregate Root - 每個策略實例的狀態容器
/// 
/// 職責：
/// - 策略的生命週期管理 (Start / Pause / Stop / Error)
/// - 配置管理 (可熱更新參數)
/// - 績效追蹤 (總交易數、勝率、累計 PnL)
/// - 發布策略事件
/// 
/// 注意：策略的"邏輯"本身不在此實現，而在 Application 層的
/// IStrategy 實作中 - 本 Aggregate 只負責狀態管理。
/// </summary>
public sealed class Strategy : AggregateRoot<Guid>
{
    public string Name { get; private set; }
    public string StrategyType { get; private set; }  // "TrendFollowing", "MeanReversion", "Arbitrage"
    public StrategyConfiguration Configuration { get; private set; }
    public StrategyStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? StoppedAt { get; private set; }
    public string? LastError { get; private set; }

    // 績效統計
    public int TotalTrades { get; private set; }
    public int WinningTrades { get; private set; }
    public int LosingTrades { get; private set; }
    public decimal CumulativePnL { get; private set; }
    public decimal MaxDrawdown { get; private set; }
    public decimal PeakPnL { get; private set; }

    public decimal WinRate => TotalTrades == 0 ? 0 : (decimal)WinningTrades / TotalTrades;

    private Strategy() : base(Guid.NewGuid())
    {
        Name = default!;
        StrategyType = default!;
        Configuration = default!;
    }

    public static Strategy Create(string name, string strategyType, StrategyConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Strategy name cannot be empty.");
        if (string.IsNullOrWhiteSpace(strategyType))
            throw new DomainException("Strategy type cannot be empty.");

        return new Strategy
        {
            Name = name,
            StrategyType = strategyType,
            Configuration = config,
            Status = StrategyStatus.Stopped,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void Start()
    {
        if (Status == StrategyStatus.Running)
            throw new DomainException($"Strategy {Name} is already running.");

        Status = StrategyStatus.Running;
        StartedAt = DateTime.UtcNow;
        LastError = null;
        RaiseDomainEvent(new StrategyStartedEvent(Id, Name));
    }

    public void Pause()
    {
        if (Status != StrategyStatus.Running)
            throw new DomainException(
                $"Can only pause a running strategy. Current: {Status}");
        Status = StrategyStatus.Paused;
    }

    public void Resume()
    {
        if (Status != StrategyStatus.Paused)
            throw new DomainException(
                $"Can only resume a paused strategy. Current: {Status}");
        Status = StrategyStatus.Running;
    }

    public void Stop(string reason)
    {
        if (Status == StrategyStatus.Stopped)
            return;
        Status = StrategyStatus.Stopped;
        StoppedAt = DateTime.UtcNow;
        RaiseDomainEvent(new StrategyStoppedEvent(Id, Name, reason));
    }

    public void ReportError(string error)
    {
        Status = StrategyStatus.Error;
        LastError = error;
        StoppedAt = DateTime.UtcNow;
        RaiseDomainEvent(new StrategyErrorEvent(Id, Name, error));
    }

    public void UpdateConfiguration(StrategyConfiguration newConfig)
    {
        // 允許熱更新 - 但策略實作需要回應變化
        Configuration = newConfig;
    }

    /// <summary>
    /// 更新交易統計 (在 Position 關閉時呼叫)
    /// </summary>
    public void RecordTradeResult(decimal pnl)
    {
        TotalTrades++;
        if (pnl > 0) WinningTrades++;
        else if (pnl < 0) LosingTrades++;

        CumulativePnL += pnl;

        if (CumulativePnL > PeakPnL)
            PeakPnL = CumulativePnL;

        var drawdown = PeakPnL - CumulativePnL;
        if (drawdown > MaxDrawdown)
            MaxDrawdown = drawdown;
    }
}
