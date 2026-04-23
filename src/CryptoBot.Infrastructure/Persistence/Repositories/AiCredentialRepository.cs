using CryptoBot.Domain.Aggregates.AiCredentialAggregate;
using CryptoBot.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence.Repositories;

public sealed class AiCredentialRepository : IAiCredentialRepository
{
    private readonly AppDbContext _ctx;

    public AiCredentialRepository(AppDbContext ctx) => _ctx = ctx;

    public Task<AiCredential?> GetByProviderAsync(string provider, CancellationToken ct = default) =>
        _ctx.Set<AiCredential>().FirstOrDefaultAsync(c => c.Provider == provider, ct);

    public async Task UpsertAsync(AiCredential credential, CancellationToken ct = default)
    {
        var existing = await _ctx.Set<AiCredential>()
            .FirstOrDefaultAsync(c => c.Provider == credential.Provider, ct).ConfigureAwait(false);

        if (existing is null)
        {
            _ctx.Set<AiCredential>().Add(credential);
            return;
        }

        existing.UpdateApiKey(credential.ApiKey);
        _ctx.Set<AiCredential>().Update(existing);
    }

    public async Task DeleteByProviderAsync(string provider, CancellationToken ct = default)
    {
        var entity = await _ctx.Set<AiCredential>()
            .FirstOrDefaultAsync(c => c.Provider == provider, ct).ConfigureAwait(false);
        if (entity is not null)
            _ctx.Set<AiCredential>().Remove(entity);
    }
}
