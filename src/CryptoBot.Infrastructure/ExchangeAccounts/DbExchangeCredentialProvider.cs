using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Infrastructure.ExchangeAccounts;

/// <summary>
/// 以 SQLite（<see cref="IExchangeAccountRepository"/>）為後端的 <see cref="IExchangeCredentialProvider"/> 實作。
///
/// 生命週期：Singleton — 跨 UI / API / HostedService 共用同一份 CredentialsChanged 事件源。
/// 內部讀 DB 時用 <see cref="IServiceScopeFactory"/> 自建 scope 取 Scoped 的 Repository。
///
/// 事件語意：UI 儲存 / Activate 某帳號後，API 端點呼叫 <see cref="NotifyCredentialsChangedAsync"/>；
/// 訂閱者（<c>BingXExchangeClient</c>）同步收到新金鑰，重建 SDK REST client。
/// </summary>
public sealed class DbExchangeCredentialProvider : IExchangeCredentialProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DbExchangeCredentialProvider> _logger;

    public event EventHandler<ExchangeCredentialsChangedEventArgs>? CredentialsChanged;

    public DbExchangeCredentialProvider(
        IServiceScopeFactory scopeFactory,
        ILogger<DbExchangeCredentialProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ExchangeCredentials> GetActiveAsync(
        ExchangeName exchange, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExchangeAccountRepository>();

        var active = await repo.GetActiveAsync(exchange, ct).ConfigureAwait(false);
        if (active is null || !active.HasCredentials)
        {
            return new ExchangeCredentials(
                Exchange: exchange,
                AccountName: string.Empty,
                ApiKey: string.Empty,
                ApiSecret: string.Empty,
                IsConfigured: false);
        }

        return new ExchangeCredentials(
            Exchange: exchange,
            AccountName: active.AccountName,
            ApiKey: active.ApiKey,
            ApiSecret: active.ApiSecret,
            IsConfigured: true);
    }

    public async Task NotifyCredentialsChangedAsync(ExchangeName exchange, CancellationToken ct = default)
    {
        var creds = await GetActiveAsync(exchange, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "ExchangeCredentials changed for {Exchange} | account={Account} | configured={Configured}",
            exchange, creds.AccountName, creds.IsConfigured);

        var handlers = CredentialsChanged;
        if (handlers is null) return;

        // 同步 fire：handler 可能是 BingXExchangeClient.ReconfigureCredentials，
        // 需要在回到呼叫端前完成 SDK client 置換。
        handlers.Invoke(this, new ExchangeCredentialsChangedEventArgs(exchange, creds));
    }
}
