namespace CryptoBot.Application.Ai;

/// <summary>
/// S30-ELITE+：AI 呼叫診斷 Ring Buffer 合約。
///
/// 每次 <see cref="IAiAdvisorService.GetAdviceAsync"/> 回傳後（無論成功或失敗），
/// 具體實作會把結果摘要推進記憶體 ring buffer（預設保留最後 20 筆）。
/// UI（/lab 的 AiAdvisorPanel、/settings/exchanges 的 Gemini 區塊）
/// 透過 <c>GET /api/ai/traces</c> 讀取這份清單，讓使用者「看到錯什麼、在哪一層錯」
/// — 取代先前只能翻 console log / 通靈的狀態。
///
/// 注意：此服務**不**持久化 — host 重啟即清空。我們接受這個 tradeoff 以保持零 schema 改動。
/// </summary>
public interface IAiAdviceTraceLog
{
    /// <summary>記錄一次 AI 呼叫結果到 ring buffer。若超過容量，最舊一筆會被擠掉。</summary>
    void Record(AiAdviceResult result);

    /// <summary>取得最近 <paramref name="limit"/> 筆呼叫紀錄，最新在前。</summary>
    IReadOnlyList<AiAdviceTrace> GetRecent(int limit);
}

/// <summary>
/// Ring buffer 中的單筆紀錄 — 是 <see cref="AiAdviceResult"/> 的 UI 友善濃縮版。
///
/// 為什麼不直接存 <see cref="AiAdviceResult"/>：
/// - Commentary 可能很長，放進 /api/ai/traces 會拖慢 JSON 傳輸
/// - SuggestedParameters 的 Dictionary 在 trace 場景只需要筆數，不需要完整結構
/// 因此這裡改存「摘要 + 完整 attempts」，attempts 才是診斷的核心。
/// </summary>
public sealed record AiAdviceTrace(
    DateTimeOffset At,
    bool Success,
    string Model,
    string? Error,
    string CommentaryPreview,
    int SuggestedParameterCount,
    IReadOnlyList<AiAttemptDiagnostic> Attempts);
