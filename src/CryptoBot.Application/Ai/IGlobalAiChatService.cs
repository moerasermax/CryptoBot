using CryptoBot.Application.Realtime;

namespace CryptoBot.Application.Ai;

/// <summary>
/// S74-C：全域常駐 AI 對話 service — 與 Blazor Server circuit（Scoped）綁定，跨頁面保留對話歷史。
///
/// 設計要點：
/// <list type="bullet">
///   <item>實際 session 真理在 <c>gemini</c> CLI 端（透過 <c>--session-id</c> 自管）；C# 端的
///         <see cref="History"/> 只是 UI 渲染副本。</item>
///   <item>每次 <see cref="SendAsync"/> 走 <c>gemini -p</c> 單發、避免 TTY 限制。</item>
///   <item>合約：永不拋。任何失敗以 <see cref="ChatMessage"/> Role="ai" + 錯誤訊息 append 進 History。</item>
/// </list>
/// </summary>
public interface IGlobalAiChatService
{
    /// <summary>
    /// 本 Scoped 對話的 session UUID — 在實例化時生成、貫穿整個 circuit。
    /// 餵給 <c>gemini --session-id &lt;uuid&gt;</c> 讓 gemini 自己保留對話歷史。
    /// </summary>
    Guid SessionUuid { get; }

    /// <summary>UI 渲染用對話歷史副本（依時間遞增）。</summary>
    IReadOnlyList<ChatMessage> History { get; }

    /// <summary>History 變更時觸發 — sidebar 訂閱後 <c>InvokeAsync(StateHasChanged)</c> 重繪。</summary>
    event Action? HistoryChanged;

    /// <summary>
    /// 送一則使用者訊息、等 AI 回應；回應內含 JSON 參數塊則自動解析填入 <see cref="ChatMessage.Payload"/>。
    /// 永不拋；失敗以含 Error 的 ai 訊息 append。
    /// </summary>
    Task SendAsync(string userText, CancellationToken ct = default);

    /// <summary>清空本地 History（gemini session-id 仍保留、下次 send 仍接續）。</summary>
    Task ClearAsync();
}

/// <summary>
/// 對話訊息單元。<see cref="HasJson"/> 為 true 時 <see cref="Payload"/> 帶結構化參數，
/// UI 顯示「🪄 套用參數至實驗室」按鈕。
/// </summary>
/// <param name="Role"><c>"user"</c> | <c>"ai"</c> | <c>"system"</c></param>
/// <param name="Text">純文字內容（AI 訊息已剝除 JSON code fence、僅留 commentary）。</param>
/// <param name="At">訊息時間（UTC）。</param>
/// <param name="HasJson">true = AI 訊息含可套用的參數 JSON；false = 純對話。</param>
/// <param name="Payload">已解析的結構化 payload；<see cref="HasJson"/>=false 時為 null。</param>
public sealed record ChatMessage(
    string Role,
    string Text,
    DateTimeOffset At,
    bool HasJson,
    ApplyAiParametersUpdate? Payload);
