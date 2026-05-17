using CryptoBot.Domain.Common;

namespace CryptoBot.Application.Common.DomainEvents;

/// <summary>
/// S77 Bug 12：Domain event handler 介面 — subscribers 實作此介面註冊處理特定 IDomainEvent。
///
/// 配合 <see cref="IDomainEventDispatcher"/> + UnitOfWork.SaveChangesAsync 內部 dispatch、
/// 達成「Aggregate state change → handler 立即執行」event-driven 設計。
///
/// 取代純依賴 Reconcile 5min tick 的設計缺陷 (Bug 11 race condition):
///   - OrderFilled 事件 → OrderFilledHandler.HandleAsync 立即補建 Position
///   - 不依賴 reconcile / WS HandleAccountUpdate 等延遲機制
/// </summary>
/// <typeparam name="TEvent">具體 IDomainEvent 子型別</typeparam>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct = default);
}
