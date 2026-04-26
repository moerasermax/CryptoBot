namespace CryptoBot.Domain.Aggregates.AiCredentialAggregate;

/// <summary>
/// AI 量化導師的成本/品質模式 — S30-FIX2 引入。
///
/// 每個 <see cref="AiCredential"/> 會帶一個 Mode 欄位，
/// <c>GeminiAiAdvisorService</c> 在每次 <c>GetAdviceAsync</c> 開頭查一次，
/// 據此從 <c>GeminiOptions</c> 挑出對應的 (Primary, Fallback) 模型配對：
///
///   Eco → 省錢、高速、略降品質（預設 Flash 系）— 開發/日常
///   Pro → 最高品質、較慢、較燒錢（預設 Pro 系）— 需要真實下單或長掃描前
///
/// Int 值固定（0/1）供 EF 作欄位儲存；不要調整次序避免破壞既存 row。
/// </summary>
public enum AiAdvisorMode
{
    /// <summary>省錢模式（預設）— Flash 主 + Flash-Lite 備。</summary>
    Eco = 0,

    /// <summary>認真模式 — Pro 系主 + Pro 備。</summary>
    Pro = 1,
}
