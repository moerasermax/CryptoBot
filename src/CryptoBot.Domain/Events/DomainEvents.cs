using CryptoBot.Domain.Common;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Events;

/// <summary>
/// Domain Event 抽象基類
/// </summary>
public abstract record DomainEventBase : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

// ===== Order Events =====

public sealed record OrderPlacedEvent(
    Guid OrderId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    Quantity Quantity,
    Price? LimitPrice) : DomainEventBase;

public sealed record OrderFilledEvent(
    Guid OrderId,
    Symbol Symbol,
    OrderSide Side,
    Quantity FilledQuantity,
    Price AveragePrice,
    decimal Commission) : DomainEventBase;

public sealed record OrderCanceledEvent(
    Guid OrderId,
    Symbol Symbol,
    string Reason) : DomainEventBase;

public sealed record OrderRejectedEvent(
    Guid OrderId,
    Symbol Symbol,
    string Reason) : DomainEventBase;

// ===== Position Events =====

public sealed record PositionOpenedEvent(
    Guid PositionId,
    Symbol Symbol,
    PositionSide Side,
    Quantity Quantity,
    Price EntryPrice,
    Leverage Leverage) : DomainEventBase;

public sealed record PositionClosedEvent(
    Guid PositionId,
    Symbol Symbol,
    PositionSide Side,
    Quantity Quantity,
    Price EntryPrice,
    Price ExitPrice,
    decimal RealizedPnL,
    string Reason) : DomainEventBase;

public sealed record StopLossTriggeredEvent(
    Guid PositionId,
    Symbol Symbol,
    Price StopPrice,
    Price CurrentPrice) : DomainEventBase;

public sealed record TakeProfitTriggeredEvent(
    Guid PositionId,
    Symbol Symbol,
    Price TargetPrice,
    Price CurrentPrice) : DomainEventBase;

// ===== Strategy Events =====

public sealed record SignalGeneratedEvent(
    Guid StrategyId,
    string StrategyName,
    Symbol Symbol,
    SignalType Signal,
    Price CurrentPrice,
    string Reason) : DomainEventBase;

public sealed record StrategyStartedEvent(
    Guid StrategyId,
    string StrategyName) : DomainEventBase;

public sealed record StrategyStoppedEvent(
    Guid StrategyId,
    string StrategyName,
    string Reason) : DomainEventBase;

public sealed record StrategyErrorEvent(
    Guid StrategyId,
    string StrategyName,
    string ErrorMessage) : DomainEventBase;

// ===== Risk Events =====

public sealed record RiskLimitExceededEvent(
    string LimitType,
    decimal LimitValue,
    decimal CurrentValue,
    string Details) : DomainEventBase;
