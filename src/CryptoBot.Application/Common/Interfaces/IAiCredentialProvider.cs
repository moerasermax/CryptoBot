using CryptoBot.Domain.Aggregates.AiCredentialAggregate;

namespace CryptoBot.Application.Common.Interfaces;

/// <summary>
/// AI 服務金鑰讀取介面 — S30 引入。
/// Infrastructure 實作（<c>DbAiCredentialProvider</c>）從 SQLite 取金鑰，
/// Application 的 <see cref="Ai.IAiAdvisorService"/> 呼叫時透過此介面取金鑰。
///
/// Provider 參數目前僅用 <c>"Gemini"</c>；未來可延伸到 "OpenAI" / "Claude" 等不共用一把金鑰的服務。
///
/// S30-FIX2：新增 <see cref="GetModeAsync"/> — 讓 service 知道目前使用者選了 Eco 還是 Pro 模式。
/// </summary>
public interface IAiCredentialProvider
{
    /// <summary>
    /// 取指定 provider 的當前金鑰；若 DB 無資料或金鑰為空字串則回 null。
    /// </summary>
    Task<string?> GetApiKeyAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// 取指定 provider 當前的 Eco/Pro 模式。若無紀錄，回 <see cref="AiAdvisorMode.Eco"/>（省錢為預設）。
    /// </summary>
    Task<AiAdvisorMode> GetModeAsync(string provider, CancellationToken ct = default);
}
