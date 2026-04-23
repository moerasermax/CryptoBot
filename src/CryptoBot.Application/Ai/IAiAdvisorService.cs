using CryptoBot.Domain.Enums;

namespace CryptoBot.Application.Ai;

/// <summary>
/// AI 量化導師服務介面 — Application 對上層（API 端點 / UI）的合約。
/// 具體實作由 Infrastructure 提供（<c>GeminiAiAdvisorService</c>）。
///
/// 契約：
/// - <see cref="GetAdviceAsync"/> 永不拋 — 任何下游失敗（金鑰缺失、HTTP 429、JSON 解析錯誤）都
///   以 <see cref="AiAdviceResult.Success"/>=false + <see cref="AiAdviceResult.Error"/> 回報，
///   讓 UI 顯示友善訊息而非 500。
/// </summary>
public interface IAiAdvisorService
{
    Task<AiAdviceResult> GetAdviceAsync(AiAdviceRequest request, CancellationToken ct = default);

    /// <summary>
    /// S30-ELITE+2：探測 AI 供應商當前對此金鑰暴露的模型清單。
    /// 用於 <c>/settings/exchanges</c> 的「🔍 探測可用模型」按鈕 —
    /// 當 Primary 回 404 時，使用者可直接看到真正可用的模型名，不必猜字串。
    /// 合約：失敗（金鑰缺、HTTP error、網路異常）永不拋，一律回 Success=false。
    /// </summary>
    Task<AiModelListResult> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>
/// AI 建議請求。
/// <paramref name="ExpectedParameterKeys"/> 限制 AI 只能回傳這些 key 的建議值 —
/// 避免幻想出策略不認得的參數名，套用時會被 form 忽略。
/// </summary>
public sealed record AiAdviceRequest(
    string StrategyKey,
    string StrategyDisplayName,
    MarketContext Context,
    IReadOnlyDictionary<string, decimal> CurrentParameters,
    IReadOnlyList<string> ExpectedParameterKeys);

/// <summary>
/// AI 建議結果。Success=false 時 <see cref="Error"/> 必填，UI 會顯示紅字且禁用「填入」。
///
/// S30-GRID：<see cref="SuggestedParameters"/> 由「點建議」升級為「網格建議」——
/// 每個參數回傳 Min/Max/Step 三元組，讓優化掃描可直接跑範圍回測。
/// 若 AI 僅能給出單點值，<see cref="ParameterGridRange"/> 會退化為 Min=Max=value, Step=1。
///
/// S30-ELITE+：<see cref="Attempts"/> 記錄「這一次呼叫」走過的模型（Primary + 選擇性的 Fallback），
/// 每筆包含 HTTP status、finishReason、safetyBlock、error message、退避次數與耗時。
/// UI 與 /api/ai/traces 利用這份資料定位「為什麼 AI 沒給結果」。
/// </summary>
public sealed record AiAdviceResult(
    bool Success,
    string Commentary,
    IReadOnlyDictionary<string, ParameterGridRange> SuggestedParameters,
    string? Error,
    string Model,
    IReadOnlyList<AiAttemptDiagnostic> Attempts);

/// <summary>
/// 一次「模型嘗試」的診斷紀錄 — 對應 <see cref="AiAdviceResult.Attempts"/> 的單筆。
///
/// 欄位語義：
/// - <paramref name="Phase"/>：<c>"primary"</c> 或 <c>"fallback"</c>，對應 S30-ELITE 的雙 Pro 鏈。
/// - <paramref name="HttpStatus"/>：最後一次 HTTP 回應的 status。<c>0</c> 代表連 HTTP 都沒回（網路 / timeout / DNS）。
/// - <paramref name="FinishReason"/>：Gemini 回的 <c>candidates[0].finishReason</c>
///   （<c>STOP</c> / <c>SAFETY</c> / <c>MAX_TOKENS</c> / <c>RECITATION</c> 等）；非 200 時為 null。
/// - <paramref name="SafetyBlock"/>：若 Gemini 有 <c>promptFeedback.blockReason</c> 或 candidate 被 safety 砍，
///   此欄會帶上 category / probability，方便判斷是「內容被拒」還是「額度 / 模型不可用」。
/// - <paramref name="ErrorMessage"/>：Gemini error.message（HTTP 非 2xx 時）或我們自己組的失敗摘要。
/// - <paramref name="RetryCount"/>：這個模型內部走過的 429/503 退避次數（0 = 一發命中，未退避）。
/// - <paramref name="DurationMs"/>：從發出第一次請求到最後一次回應的總耗時，含退避等待。
/// </summary>
public sealed record AiAttemptDiagnostic(
    string Model,
    string Phase,
    int HttpStatus,
    string? FinishReason,
    string? SafetyBlock,
    string? ErrorMessage,
    int RetryCount,
    int DurationMs,
    DateTimeOffset At);

/// <summary>
/// <see cref="IAiAdvisorService.ListModelsAsync"/> 的結果。失敗時 <see cref="Models"/> 為空，<see cref="Error"/> 帶原因。
/// </summary>
public sealed record AiModelListResult(
    bool Success,
    IReadOnlyList<AiModelInfo> Models,
    string? Error);

/// <summary>
/// Google ListModels 回應的一筆模型資訊。欄位對齊 Gemini REST
/// （<c>name</c> / <c>displayName</c> / <c>version</c> /
/// <c>inputTokenLimit</c> / <c>outputTokenLimit</c> / <c>supportedGenerationMethods</c>），
/// 其他非診斷核心的欄位不帶出。
/// </summary>
public sealed record AiModelInfo(
    string Name,
    string? DisplayName,
    string? Version,
    long? InputTokenLimit,
    long? OutputTokenLimit,
    IReadOnlyList<string> SupportedMethods);

/// <summary>
/// AI 建議的單一參數網格範圍。優化掃描會以此產生 ( (Max-Min)/Step + 1 ) 個回測點。
/// Min &lt;= Max 且 Step &gt; 0 由上層（Sanitize / 表單驗證）保證，此 record 本身不驗證。
/// </summary>
public sealed record ParameterGridRange(decimal Min, decimal Max, decimal Step);
