using CryptoBot.Application.Common.DomainEvents;
using CryptoBot.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Infrastructure.DomainEvents;

/// <summary>
/// S77 Bug 12：<see cref="IDomainEventDispatcher"/> 預設實作 — reflection-based handler 分派。
///
/// 流程：
/// 1. UnitOfWork.SaveChangesAsync 內收集 ChangeTracker 內 AggregateRoot 的 DomainEvents
/// 2. SaveChanges 成功後呼叫 DispatchAsync(events)
/// 3. 對每個 event: GetServices(IDomainEventHandler&lt;T&gt;) 拿所有 handlers → 逐一 HandleAsync
/// 4. handler 失敗 catch + LogError、不向上傳遞 (避免 SaveChanges 因 side-effect 失敗)
///
/// 限制 (minimal 設計):
///   - reflection 開銷 (production 通常可忽略, event 頻率低)
///   - 不保證 order (多 handler 同一 event 平行 / 序列由 ServiceProvider 順序決定)
///   - handler 異常隔離 (一個 handler 失敗不影響其他 handler)
/// </summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DomainEventDispatcher> _logger;

    public DomainEventDispatcher(IServiceProvider serviceProvider, ILogger<DomainEventDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task DispatchAsync(IReadOnlyList<IDomainEvent> domainEvents, CancellationToken ct = default)
    {
        if (domainEvents.Count == 0) return;

        foreach (var domainEvent in domainEvents)
        {
            var eventType = domainEvent.GetType();
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
            var handlers = _serviceProvider.GetServices(handlerType);

            foreach (var handler in handlers)
            {
                if (handler is null) continue;
                try
                {
                    var method = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync));
                    if (method is null) continue;
                    var task = method.Invoke(handler, new object[] { domainEvent, ct }) as Task;
                    if (task is not null) await task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[DOMAIN_EVENT] Handler {Handler} failed for event {Event} — isolated (does not affect SaveChanges).",
                        handler.GetType().Name, eventType.Name);
                }
            }
        }
    }
}
