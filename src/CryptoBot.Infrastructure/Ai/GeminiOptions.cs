namespace CryptoBot.Infrastructure.Ai;

/// <summary>
/// S30-FIX2：Gemini 模型配置改為「Eco / Pro 雙模式各自帶 Primary+Fallback」。
/// <see cref="GeminiAiAdvisorService"/> 每次 <c>GetAdviceAsync</c> 開頭向
/// <see cref="Application.Common.Interfaces.IAiCredentialProvider"/> 查使用者當前 Mode，
/// 再從本 Options 挑出對應的 (Primary, Fallback) 配對。單次請求內不換模式，避免混雜結果。
///
/// 退避政策保持 S30-ELITE 規則：先打 Primary，指數退避期內若仍收 429/503/404，
/// 停 3s 切 Fallback 再跑一次完整退避週期。
///
/// 模型歷程：
///   1.5-flash（S30） → 3.1-flash（S30-FIX） → 2.5-pro（S30-PRO）
///   → 2.5-flash-lite（S30-LITE） → 3.1-pro + 2.5-pro 雙 Pro（S30-ELITE）
///   → 3.1-pro-preview + 2.5-pro（S30-ELITE+）
///   → Eco/Pro 雙配對（S30-FIX2）
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>
    /// <b>Eco 模式</b>主模型 — 省錢、低延遲。預設 <c>gemini-2.5-flash</c>。
    /// 開發期間 / 反覆按 🪄 試水溫時用這個，單次輸入/輸出 token 單價約為 Pro 系的 1/10。
    /// </summary>
    public string EcoPrimaryModel { get; set; } = "gemini-2.5-flash";

    /// <summary>
    /// <b>Eco 模式</b>備援模型 — 比 <see cref="EcoPrimaryModel"/> 更小更便宜。
    /// 預設 <c>gemini-2.5-flash-lite</c>。
    /// </summary>
    public string EcoFallbackModel { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>
    /// <b>Pro 模式</b>主模型 — 最高品質、較慢、較燒錢。預設 <c>gemini-3.1-pro-preview</c>
    /// （付費 Pay-as-you-go；注意需搭 <see cref="ApiVersion"/>=<c>v1beta</c> 才查得到，v1 端點會 404）。
    /// 真的要把建議送進長時間優化掃描時用這個。
    /// </summary>
    public string ProPrimaryModel { get; set; } = "gemini-3.1-pro-preview";

    /// <summary>
    /// <b>Pro 模式</b>備援模型 — 當 <see cref="ProPrimaryModel"/> 429/503/404 時接手。
    /// 預設 <c>gemini-2.5-pro</c>（已進入穩定 v1，足以作為 preview 掛掉時的後盾）。
    /// </summary>
    public string ProFallbackModel { get; set; } = "gemini-2.5-pro";

    /// <summary>
    /// Google AI REST 根 host（不含 API version 與 <c>/models/</c> 子路徑）。
    /// 預設 <c>https://generativelanguage.googleapis.com</c>；實際打出去的 URL 由
    /// <see cref="BaseUrl"/> + <see cref="ApiVersion"/> + <c>/models/{model}:generateContent</c> 拼成。
    /// 不應在此放入 <c>/v1/</c> 或 <c>/v1beta/</c>——請透過 <see cref="ApiVersion"/> 控制版本。
    /// </summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>
    /// Google GenAI API 版本。預設 <c>v1</c>（穩定版，維持向後相容）。
    ///
    /// 何時設 <c>v1beta</c>：當需要打到尚未進入穩定版的 preview 模型（如 <c>gemini-3.1-pro-preview</c>）
    /// 時，v1 端點會回 404「not found for API version v1」；切到 v1beta 才查得到。
    /// 代價是 v1beta schema 不保證穩定，Google 隨時可能破壞性變動。
    ///
    /// 覆蓋方式：
    ///   appsettings.json  →  "Gemini": { "ApiVersion": "v1beta" }
    ///   環境變數         →  Gemini__ApiVersion=v1beta
    /// </summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>
    /// HTTP timeout（秒）— <b>單次</b> HTTP 請求；不含 <see cref="GeminiAiAdvisorService"/> 的退避等待。
    /// 3.1-pro-preview 回應較慢，維持 30s 保守值。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gemini 單次回應的最大 output tokens。S30-ELITE+2：預設 <b>8192</b>。
    ///
    /// 為什麼這麼高：S30-ELITE prompt 要求「行情定性 + 雙目標分析 + 多參數 min/max/step JSON」，
    /// 先前 1024 會被 Pro 級模型的詳細輸出撞到 <c>finishReason=MAX_TOKENS</c>，
    /// 結果 <c>candidates[0].content.parts[0].text</c> 空 → UI 顯示「沒有回傳內容」誤導使用者。
    /// Gemini 2.5 Pro 輸出上限 8192；3.x 系列更高，但 8192 已足夠涵蓋任何量化 JSON 建議。
    ///
    /// 可透過 <c>Gemini__MaxOutputTokens</c> 環境變數覆蓋。
    /// </summary>
    public int MaxOutputTokens { get; set; } = 8192;
}
