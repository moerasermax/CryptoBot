using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core / SQLite 版本的 <see cref="IOrderRepository"/>。
/// Add/Update 不直接送 DB（保留給 <see cref="UnitOfWork.SaveChangesAsync"/> 一次提交）。
/// </summary>
public sealed class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _ctx;

    public OrderRepository(AppDbContext ctx) => _ctx = ctx;

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _ctx.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetByExchangeOrderIdAsync(string exchangeOrderId, CancellationToken ct = default) =>
        _ctx.Orders.FirstOrDefaultAsync(o => o.ExchangeOrderId == exchangeOrderId, ct);

    public async Task<IReadOnlyList<Order>> GetActiveOrdersAsync(CancellationToken ct = default)
    {
        var list = await _ctx.Orders
            .Where(o => o.Status == Domain.Enums.OrderStatus.New
                     || o.Status == Domain.Enums.OrderStatus.PartiallyFilled)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        // Symbol 走 ValueConverter → 字串欄位；用 EF.Property 比對轉換後的值，
        // 避免 ValueObject.== / .Equals 在表達式樹中無法翻譯為 SQL。
        var symbolStr = symbol.BingXFormat;
        var list = await _ctx.Orders
            .Where(o => EF.Property<string>(o, nameof(Order.Symbol)) == symbolStr)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Order>> GetByStrategyIdAsync(Guid strategyId, CancellationToken ct = default)
    {
        var list = await _ctx.Orders
            .Where(o => o.StrategyId == strategyId)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        var list = await _ctx.Orders
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public Task AddAsync(Order order, CancellationToken ct = default)
    {
        _ctx.Orders.Add(order);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        // 對於由 EF tracking 中已存在的實例，呼叫 Update 會把整列標記為 Modified；
        // 對於由外部以新 instance 帶 id 進來的情況，也會被當作 attached + Modified。
        _ctx.Orders.Update(order);
        return Task.CompletedTask;
    }
}
