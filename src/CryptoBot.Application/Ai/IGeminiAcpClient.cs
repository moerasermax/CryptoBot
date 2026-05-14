namespace CryptoBot.Application.Ai;

/// <summary>
/// S75：透過 <c>gemini --acp</c> 長連接 IPC 與 Gemini CLI 對話的抽象。
///
/// 取代 S74-C 的 <c>gemini -p</c> single-shot 模式，消除冷啟動延遲（每次 spawn ~1-3s）
/// 與 Win32 CreateProcess 28K wchar 字串上限。
///
/// 設計要點：
/// <list type="bullet">
///   <item>底層為 JSON-RPC 2.0 over stdin/stdout（line-delimited），實作細節封裝於 Infrastructure 層。</item>
///   <item>抽象不洩漏 <c>System.Diagnostics.Process</c> / <c>JsonElement</c> 等 Infrastructure 型別（IRON ⑥）。</item>
///   <item>Lazy session：第一次 <see cref="SendPromptAsync"/> 觸發 initialize + authenticate + session/new；
///         之後同 instance 多次呼叫共用同一 session。</item>
///   <item>序列化呼叫：實作端內建 lock；同一 instance 不支援併發 SendPromptAsync。</item>
///   <item>cwd 隔離：實作端啟動子 process 於 dedicated temp 目錄、避免 cwd 內 <c>GEMINI.md</c> auto-load。</item>
///   <item>生命週期：實作 <see cref="IAsyncDisposable"/>，dispose 時關閉 stdin → process clean exit，
///         fallback process tree kill。</item>
/// </list>
///
/// 合約：<see cref="SendPromptAsync"/> 可能拋出 — 上層應 catch 並將錯誤轉為 user-facing 訊息
/// （對齊 <see cref="IGlobalAiChatService.SendAsync"/> 永不拋契約）。
/// </summary>
public interface IGeminiAcpClient : IAsyncDisposable
{
    /// <summary>
    /// 確保 ACP session 已就緒（initialize + authenticate + session/new）。
    /// Idempotent — 多次呼叫只實際初始化一次。<see cref="SendPromptAsync"/> 內部會自動呼叫，
    /// 通常不需手動 invoke。
    /// </summary>
    Task EnsureSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// 送 prompt 給 ACP session、逐 chunk yield 文字片段（streaming）。
    ///
    /// 行為：
    /// <list type="bullet">
    ///   <item>內部會等所有 <c>session/update</c> notifications（agent_message_chunk 等）+ final response。</item>
    ///   <item>yield 的字串是文字 chunks（已從 session/update 抽出）；caller 可累積為完整回應。</item>
    ///   <item>遇 429 / 其他 model error → 拋例外（Phase 2 不做 retry；Phase 3 加固）。</item>
    ///   <item>遇 <paramref name="ct"/> cancel → 拋 <see cref="OperationCanceledException"/>，
    ///         in-flight prompt 因 ACP 無 cancel method 而無法精確取消，
    ///         上層應視情況 dispose 重啟（Phase 1 §7.3 結論）。</item>
    /// </list>
    /// </summary>
    /// <param name="text">prompt 文字（單一 text content part；image/audio 暫不支援）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>async stream of text chunks。</returns>
    IAsyncEnumerable<string> SendPromptAsync(string text, CancellationToken ct = default);
}
