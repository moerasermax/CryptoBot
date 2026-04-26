using CryptoBot.Domain.Common;
using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.Aggregates.AiCredentialAggregate;

/// <summary>
/// AI 服務金鑰 Aggregate Root — S30 引入。
///
/// 設計：Provider 字串（如 "Gemini"）為邏輯唯一鍵；儲存層以 Guid Id 作 PK，Provider 加 unique index。
/// Aggregate 提供 <c>Upsert</c> 語意給 Repository 使用，但為了方便 EF 追蹤，狀態更新以 in-place
/// <see cref="UpdateApiKey"/>、<see cref="ChangeMode"/> 呈現。
///
/// S30-FIX2：新增 <see cref="Mode"/> 欄位，用於 Eco/Pro 模式切換（省錢 vs 認真），
/// 由 <c>GeminiAiAdvisorService</c> 讀取後對應到不同 (Primary, Fallback) 模型配對。
///
/// 安全提醒：與 ExchangeAccount 相同 — 金鑰在 SQLite 以明文存放，保護仰賴檔案權限。
/// </summary>
public sealed class AiCredential : AggregateRoot<Guid>
{
    /// <summary>目前支援的 Provider — 與 <see cref="GeminiProvider"/> 常數比對。</summary>
    public const string GeminiProvider = "Gemini";

    public string Provider { get; private set; }
    public string ApiKey { get; private set; }

    /// <summary>Eco（省錢）/ Pro（認真）切換。預設 <see cref="AiAdvisorMode.Eco"/>。</summary>
    public AiAdvisorMode Mode { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    private AiCredential() : base(Guid.NewGuid())
    {
        Provider = default!;
        ApiKey = default!;
    }

    public static AiCredential Create(string provider, string apiKey, AiAdvisorMode mode = AiAdvisorMode.Eco)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new DomainException("Provider cannot be empty.");
        if (provider.Length > 32)
            throw new DomainException("Provider name must be ≤ 32 chars.");

        return new AiCredential
        {
            Provider = provider.Trim(),
            ApiKey = apiKey ?? string.Empty,
            Mode = mode,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// 更新金鑰。空字串視為「清除」— UI 送空值即表示要移除金鑰。
    /// </summary>
    public void UpdateApiKey(string? newApiKey)
    {
        ApiKey = newApiKey ?? string.Empty;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// S30-FIX2：切換 Eco/Pro 模式。Model 配對由 Infrastructure 決定，此處只持有選擇本身。
    /// </summary>
    public void ChangeMode(AiAdvisorMode newMode)
    {
        Mode = newMode;
        UpdatedAt = DateTime.UtcNow;
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
}
