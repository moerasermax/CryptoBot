using CryptoBot.Domain.Common;

namespace CryptoBot.Application.Common.DomainEvents;

/// <summary>
/// S77 Bug 12：Domain event dispatcher — 在 UnitOfWork.SaveChangesAsync 後被呼叫,
/// 從 ChangeTracker 收集 pending IDomainEvent + 分派到所有對應 IDomainEventHandler。
///
/// 設計選擇 (minimal, 不引入 MediatR / 大型 lib):
///   - 用 IServiceProvider GetServices(typeof(IDomainEventHandler&lt;T&gt;)) 反射拿 handlers
///   - SaveChanges 後 dispatch (避免 in-transaction 副作用 / circular write)
///   - handler 失敗只 log error、不擋 SaveChanges (event 是 side-effect, 不該影響主 transaction)
/// </summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyList<IDomainEvent> domainEvents, CancellationToken ct = default);
}
