using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.AiCredentialAggregate;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.Repositories;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/ai/credentials/*</c> — S30 AI 金鑰管理端點（目前僅 Gemini）。
///
/// <list type="bullet">
///   <item><c>GET    /api/ai/credentials/{provider}</c> — 狀態查詢（含遮罩預覽、更新時間、Eco/Pro 模式）</item>
///   <item><c>PUT    /api/ai/credentials/{provider}</c> — 寫入或更新金鑰（保留現有 Mode）</item>
///   <item><c>PUT    /api/ai/credentials/{provider}/mode</c> — 切換 Eco/Pro 模式（不動金鑰）</item>
///   <item><c>DELETE /api/ai/credentials/{provider}</c> — 清除金鑰</item>
/// </list>
///
/// 設計同 <see cref="ExchangeAccountEndpoints"/>：金鑰永不回前端，只回 <c>sk-••••abcd</c> 樣式預覽。
/// </summary>
public static class AiCredentialEndpoints
{
    public static IEndpointRouteBuilder MapAiCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai/credentials").WithTags("AiCredentials");

        group.MapGet("/{provider}", async (
            string provider,
            IAiCredentialRepository repo,
            CancellationToken ct) =>
        {
            var cred = await repo.GetByProviderAsync(provider, ct).ConfigureAwait(false);
            return Results.Ok(ToDto(provider, cred));
        });

        group.MapPut("/{provider}", async (
            string provider,
            UpsertAiCredentialRequest body,
            IAiCredentialRepository repo,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.ApiKey))
                return Results.BadRequest(new { error = "ApiKey must not be empty." });

            AiCredential cred;
            try
            {
                cred = AiCredential.Create(provider, body.ApiKey.Trim());
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await repo.UpsertAsync(cred, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            var saved = await repo.GetByProviderAsync(provider, ct).ConfigureAwait(false);
            return Results.Ok(ToDto(provider, saved));
        });

        // S30-FIX2：Eco/Pro 切換獨立成端點 — 不動金鑰，只翻 Mode 欄位。
        // 既存 row 直接 ChangeMode；無 row 則用空金鑰 + Mode 開新筆，讓使用者可在還沒填金鑰前就先決定模式。
        group.MapPut("/{provider}/mode", async (
            string provider,
            UpdateAiModeRequest body,
            IAiCredentialRepository repo,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<AiAdvisorMode>(body.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = $"Mode must be 'Eco' or 'Pro' (got '{body.Mode}')." });

            var existing = await repo.GetByProviderAsync(provider, ct).ConfigureAwait(false);
            if (existing is null)
            {
                AiCredential created;
                try
                {
                    created = AiCredential.Create(provider, string.Empty, mode);
                }
                catch (DomainException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                await repo.UpsertAsync(created, ct).ConfigureAwait(false);
            }
            else
            {
                existing.ChangeMode(mode);
            }

            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            var saved = await repo.GetByProviderAsync(provider, ct).ConfigureAwait(false);
            return Results.Ok(ToDto(provider, saved));
        });

        group.MapDelete("/{provider}", async (
            string provider,
            IAiCredentialRepository repo,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            await repo.DeleteByProviderAsync(provider, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        return app;
    }

    private static AiCredentialDto ToDto(string provider, AiCredential? cred)
    {
        // S30-FIX2：即使 cred 存在但無金鑰，也要把 Mode 帶回去，讓 UI 在「還沒填金鑰」狀態下仍能顯示使用者選擇的模式。
        if (cred is null)
        {
            return new AiCredentialDto(
                Provider: provider,
                IsConfigured: false,
                KeyPreview: string.Empty,
                Mode: AiAdvisorMode.Eco.ToString(),
                UpdatedAt: null);
        }

        return new AiCredentialDto(
            Provider: cred.Provider,
            IsConfigured: cred.HasKey,
            KeyPreview: cred.HasKey ? PreviewKey(cred.ApiKey) : string.Empty,
            Mode: cred.Mode.ToString(),
            UpdatedAt: cred.UpdatedAt);
    }

    private static string PreviewKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (key.Length <= 8) return "••••••";
        return $"{key[..4]}…{key[^4..]}";
    }
}

public sealed record UpsertAiCredentialRequest(string ApiKey);

/// <summary>S30-FIX2：Eco/Pro 模式切換 — 字串對應 <see cref="AiAdvisorMode"/>。</summary>
public sealed record UpdateAiModeRequest(string Mode);

public sealed record AiCredentialDto(
    string Provider,
    bool IsConfigured,
    string KeyPreview,
    string Mode,
    DateTime? UpdatedAt);
