namespace CryptoBot.Application.Ai;

/// <summary>
/// 沒有配置 AI 金鑰時的後備實作 — 永遠回 Success=false + 「未配置」訊息。
/// Application 層用 <c>TryAddSingleton</c> 註冊；Infrastructure 偵測到金鑰時會 Replace 掉。
/// </summary>
public sealed class NoOpAiAdvisorService : IAiAdvisorService
{
    public Task<AiAdviceResult> GetAdviceAsync(AiAdviceRequest request, CancellationToken ct = default) =>
        Task.FromResult(new AiAdviceResult(
            Success: false,
            Commentary: string.Empty,
            SuggestedParameters: new Dictionary<string, ParameterGridRange>(),
            Error: "AI Advisor 尚未配置 — 請至「交易所金鑰管理」填入 Gemini API Key。",
            Model: "noop",
            Attempts: Array.Empty<AiAttemptDiagnostic>()));

    public Task<AiModelListResult> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(new AiModelListResult(
            Success: false,
            Models: Array.Empty<AiModelInfo>(),
            Error: "AI Advisor 尚未配置 — 請至「交易所金鑰管理」填入 Gemini API Key。"));
}
