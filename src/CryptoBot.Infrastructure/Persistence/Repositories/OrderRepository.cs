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
        // S99 HOTFIX：同 PositionRepository.GetOpenPositionsBySymbolAsync — 原本用
        // `EF.Property<string>(o, nameof(Order.Symbol)) == symbolStr` 在 SymbolConverter 下
        // 會拋 `InvalidCastException: Invalid cast from 'System.String' to 'Symbol'`
        // （README §S17.5 已記錄該雷）。改成依 CreatedAt 粗篩近窗內的 Order，再在
        // 記憶體用 Symbol.Equals 過濾，徹底迴避 EF 對 value-converted VO 的翻譯邊角案例。
        // Orders 表雖比 Positions 大，但近 30 天視窗 + IsActive/Status 的常態用法皆另有入口
        // （GetActiveOrdersAsync / GetByExchangeOrderIdAsync），這裡僅作為「歷史查」支援路徑。
        var list = await _ctx.Orders
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        return list.Where(o => o.Symbol.Equals(symbol)).ToList();
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

    /// <summary>
    /// S53 T3：縮小事務邊界。
    /// 若 entity 已被 tracking（典型：Repository 先 Get 過），不要走 <c>DbSet.Update()</c> —
    /// 那會把所有欄位標為 Modified、產出整行 UPDATE，跟 AccountSynchronizer 的 WS
    /// order tick UPDATE 搶鎖窗口會被放大。改讓 EF 自己的 change tracker 偵測
    /// 只 UPDATE 實際變動的欄位（Status / FilledQuantity / AverageFillPrice 等）。
    /// Detached 時仍 reattach 一次。
    /// </summary>
    public Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        var entry = _ctx.Entry(order);
        if (entry.State == EntityState.Detached)
            _ctx.Orders.Update(order);
        return Task.CompletedTask;
    }
}
