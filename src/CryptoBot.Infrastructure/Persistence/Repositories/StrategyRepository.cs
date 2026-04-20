using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence.Repositories;

public sealed class StrategyRepository : IStrategyRepository
{
    private readonly AppDbContext _ctx;

    public StrategyRepository(AppDbContext ctx) => _ctx = ctx;

    public Task<Strategy?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _ctx.Strategies.FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Strategy?> GetByNameAsync(string name, CancellationToken ct = default) =>
        _ctx.Strategies.FirstOrDefaultAsync(s => s.Name == name, ct);

    public async Task<IReadOnlyList<Strategy>> GetAllAsync(CancellationToken ct = default)
    {
        var list = await _ctx.Strategies.ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Strategy>> GetByStatusAsync(
        StrategyStatus status, CancellationToken ct = default)
    {
        var list = await _ctx.Strategies
            .Where(s => s.Status == status)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public Task AddAsync(Strategy strategy, CancellationToken ct = default)
    {
        _ctx.Strategies.Add(strategy);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Strategy strategy, CancellationToken ct = default)
    {
        _ctx.Strategies.Update(strategy);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _ctx.Strategies.FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);
        if (entity is not null)
            _ctx.Strategies.Remove(entity);
    }
}
