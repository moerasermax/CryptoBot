using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence.Repositories;

public sealed class ExchangeAccountRepository : IExchangeAccountRepository
{
    private readonly AppDbContext _ctx;

    public ExchangeAccountRepository(AppDbContext ctx) => _ctx = ctx;

    public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _ctx.Set<ExchangeAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<ExchangeAccount?> GetActiveAsync(ExchangeName exchange, CancellationToken ct = default) =>
        _ctx.Set<ExchangeAccount>()
            .FirstOrDefaultAsync(a => a.Exchange == exchange && a.IsActive, ct);

    public async Task<IReadOnlyList<ExchangeAccount>> GetAllAsync(CancellationToken ct = default) =>
        await _ctx.Set<ExchangeAccount>()
            .OrderBy(a => a.Exchange).ThenBy(a => a.AccountName)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ExchangeAccount>> GetByExchangeAsync(
        ExchangeName exchange, CancellationToken ct = default) =>
        await _ctx.Set<ExchangeAccount>()
            .Where(a => a.Exchange == exchange)
            .OrderBy(a => a.AccountName)
            .ToListAsync(ct).ConfigureAwait(false);

    public Task AddAsync(ExchangeAccount account, CancellationToken ct = default)
    {
        _ctx.Set<ExchangeAccount>().Add(account);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ExchangeAccount account, CancellationToken ct = default)
    {
        _ctx.Set<ExchangeAccount>().Update(account);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _ctx.Set<ExchangeAccount>()
            .FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
        if (entity is not null)
            _ctx.Set<ExchangeAccount>().Remove(entity);
    }

    public async Task SetActiveAsync(Guid id, CancellationToken ct = default)
    {
        var target = await _ctx.Set<ExchangeAccount>()
            .FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ExchangeAccount {id} not found.");

        var siblings = await _ctx.Set<ExchangeAccount>()
            .Where(a => a.Exchange == target.Exchange && a.Id != id && a.IsActive)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var s in siblings) s.Deactivate();
        target.Activate();
    }
}
