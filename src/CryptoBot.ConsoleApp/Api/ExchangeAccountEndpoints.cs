using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.Repositories;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/exchange-accounts/*</c> — S24 金鑰管理端點。
///
/// <list type="bullet">
///   <item><c>GET    /api/exchange-accounts</c> — 列所有帳號（ApiSecret 遮罩）</item>
///   <item><c>GET    /api/exchange-accounts/status/{exchange}</c> — 該交易所是否已配置 active 金鑰</item>
///   <item><c>POST   /api/exchange-accounts</c> — 新增一筆，option activate</item>
///   <item><c>PUT    /api/exchange-accounts/{id}</c> — 更新金鑰 / 名稱</item>
///   <item><c>POST   /api/exchange-accounts/{id}/activate</c> — 切換 active（同交易所互斥）</item>
///   <item><c>DELETE /api/exchange-accounts/{id}</c></item>
/// </list>
/// </summary>
public static class ExchangeAccountEndpoints
{
    private const string SecretMask = "••••••";

    public static IEndpointRouteBuilder MapExchangeAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/exchange-accounts").WithTags("ExchangeAccounts");

        group.MapGet("/", async (IExchangeAccountRepository repo, CancellationToken ct) =>
        {
            var list = await repo.GetAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(list.Select(ToDto).ToList());
        });

        group.MapGet("/status/{exchange}", async (
            string exchange,
            IExchangeCredentialProvider provider,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<ExchangeName>(exchange, ignoreCase: true, out var ex))
                return Results.BadRequest(new { error = $"Unknown exchange: {exchange}" });

            var creds = await provider.GetActiveAsync(ex, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                exchange = ex.ToString(),
                isConfigured = creds.IsConfigured,
                accountName = creds.AccountName,
            });
        });

        group.MapPost("/", async (
            CreateExchangeAccountRequest body,
            IExchangeAccountRepository repo,
            IExchangeCredentialProvider provider,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<ExchangeName>(body.Exchange, ignoreCase: true, out var exchange))
                return Results.BadRequest(new { error = $"Unknown exchange: {body.Exchange}" });
            if (string.IsNullOrWhiteSpace(body.ApiKey) || string.IsNullOrWhiteSpace(body.ApiSecret))
                return Results.BadRequest(new { error = "ApiKey and ApiSecret must not be empty." });

            ExchangeAccount account;
            try
            {
                account = ExchangeAccount.Create(
                    exchange: exchange,
                    accountName: body.AccountName,
                    apiKey: body.ApiKey,
                    apiSecret: body.ApiSecret,
                    isActive: false);
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await repo.AddAsync(account, ct).ConfigureAwait(false);

            if (body.Activate)
            {
                await uow.SaveChangesAsync(ct).ConfigureAwait(false);
                await repo.SetActiveAsync(account.Id, ct).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            if (body.Activate)
                await provider.NotifyCredentialsChangedAsync(exchange, ct).ConfigureAwait(false);

            return Results.Created($"/api/exchange-accounts/{account.Id}", ToDto(account));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateExchangeAccountRequest body,
            IExchangeAccountRepository repo,
            IExchangeCredentialProvider provider,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var account = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound(new { error = "Account not found." });

            try
            {
                account.UpdateCredentials(body.AccountName, body.ApiKey, body.ApiSecret);
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await repo.UpdateAsync(account, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            if (account.IsActive)
                await provider.NotifyCredentialsChangedAsync(account.Exchange, ct).ConfigureAwait(false);

            return Results.Ok(ToDto(account));
        });

        group.MapPost("/{id:guid}/activate", async (
            Guid id,
            IExchangeAccountRepository repo,
            IExchangeCredentialProvider provider,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var account = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound(new { error = "Account not found." });

            try
            {
                await repo.SetActiveAsync(id, ct).ConfigureAwait(false);
                await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await provider.NotifyCredentialsChangedAsync(account.Exchange, ct).ConfigureAwait(false);
            return Results.Ok(new { activated = id });
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            IExchangeAccountRepository repo,
            IExchangeCredentialProvider provider,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var account = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound(new { error = "Account not found." });

            var wasActive = account.IsActive;
            var exchange = account.Exchange;

            await repo.DeleteAsync(id, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            if (wasActive)
                await provider.NotifyCredentialsChangedAsync(exchange, ct).ConfigureAwait(false);

            return Results.NoContent();
        });

        return app;
    }

    private static ExchangeAccountDto ToDto(ExchangeAccount a) => new(
        Id: a.Id,
        Exchange: a.Exchange.ToString(),
        AccountName: a.AccountName,
        ApiKeyPreview: PreviewKey(a.ApiKey),
        ApiSecretMask: string.IsNullOrEmpty(a.ApiSecret) ? string.Empty : SecretMask,
        IsActive: a.IsActive,
        CreatedAt: a.CreatedAt,
        UpdatedAt: a.UpdatedAt);

    private static string PreviewKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (key.Length <= 8) return SecretMask;
        return $"{key[..4]}…{key[^4..]}";
    }
}

public sealed record CreateExchangeAccountRequest(
    string Exchange,
    string AccountName,
    string ApiKey,
    string ApiSecret,
    bool Activate);

public sealed record UpdateExchangeAccountRequest(
    string? AccountName,
    string? ApiKey,
    string? ApiSecret);

public sealed record ExchangeAccountDto(
    Guid Id,
    string Exchange,
    string AccountName,
    string ApiKeyPreview,
    string ApiSecretMask,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);
