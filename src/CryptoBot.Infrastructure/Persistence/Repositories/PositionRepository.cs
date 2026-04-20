using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence.Repositories;

public sealed class PositionRepository : IPositionRepository
{
    private readonly AppDbContext _ctx;

    public PositionRepository(AppDbContext ctx) => _ctx = ctx;

    public Task<Position?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _ctx.Positions.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        var list = await _ctx.Positions
            .Where(p => !p.IsClosed)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Position>> GetOpenPositionsBySymbolAsync(
        Symbol symbol, CancellationToken ct = default)
    {
        var symbolStr = symbol.BingXFormat;
        var list = await _ctx.Positions
            .Where(p => !p.IsClosed
                     && EF.Property<string>(p, nameof(Position.Symbol)) == symbolStr)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Position>> GetByStrategyIdAsync(
        Guid strategyId, bool includeClosedPositions = false, CancellationToken ct = default)
    {
        var query = _ctx.Positions.Where(p => p.StrategyId == strategyId);
        if (!includeClosedPositions)
            query = query.Where(p => !p.IsClosed);

        var list = await query.ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Position>> GetClosedPositionsInRangeAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var list = await _ctx.Positions
            .Where(p => p.IsClosed
                     && p.ClosedAt != null
                     && p.ClosedAt >= from
                     && p.ClosedAt < to)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public Task AddAsync(Position position, CancellationToken ct = default)
    {
        _ctx.Positions.Add(position);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Position position, CancellationToken ct = default)
    {
        _ctx.Positions.Update(position);
        return Task.CompletedTask;
    }
}
