using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.AiCredentialAggregate;
using CryptoBot.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.Infrastructure.Ai;

/// <summary>
/// 從 SQLite 讀 AI 金鑰的 Provider。Singleton — 內部用 <see cref="IServiceScopeFactory"/>
/// 建 scope 取 Scoped 的 Repository，與 <c>DbExchangeCredentialProvider</c> 同一模式。
///
/// 每次呼叫 <see cref="GetApiKeyAsync"/> / <see cref="GetModeAsync"/> 都查 DB，保證 UI 上改完下一次 AI
/// 請求就生效，不需額外的 cache-invalidate 事件。
/// </summary>
public sealed class DbAiCredentialProvider : IAiCredentialProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbAiCredentialProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string?> GetApiKeyAsync(string provider, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAiCredentialRepository>();
        var cred = await repo.GetByProviderAsync(provider, ct).ConfigureAwait(false);
        if (cred is null || !cred.HasKey) return null;
        return cred.ApiKey;
    }

    public async Task<AiAdvisorMode> GetModeAsync(string provider, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAiCredentialRepository>();
        var cred = await repo.GetByProviderAsync(provider, ct).ConfigureAwait(false);
        return cred?.Mode ?? AiAdvisorMode.Eco;
    }
}
