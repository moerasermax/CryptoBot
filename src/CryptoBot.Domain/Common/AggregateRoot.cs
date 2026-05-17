namespace CryptoBot.Domain.Common;

/// <summary>
/// 領域事件標記介面 - 代表領域中發生過的事實
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTime OccurredOn { get; }
}

/// <summary>
/// S77 Bug 12: AggregateRoot 攜帶 DomainEvents 的 non-generic 抽象 — 給 UnitOfWork 用
/// <c>OfType&lt;IAggregateRootWithEvents&gt;()</c> 收集所有 aggregate 的 events 統一 dispatch。
/// 不帶泛型參數方便 reflection / pattern matching、避免 <c>AggregateRoot&lt;?&gt;</c> 不可表達問題。
/// </summary>
public interface IAggregateRootWithEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

/// <summary>
/// 聚合根 (Aggregate Root) 抽象基類。
/// 聚合根是聚合的唯一外部入口，負責維護聚合內的不變式 (Invariants)，
/// 並管理領域事件的發布。
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRootWithEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = new();
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected AggregateRoot(TId id) : base(id) { }
    protected AggregateRoot() : base() { }

    protected void RaiseDomainEvent(IDomainEvent domainEvent) =>
        _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
