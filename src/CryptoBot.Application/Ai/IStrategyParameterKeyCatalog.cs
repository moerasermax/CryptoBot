namespace CryptoBot.Application.Ai;

/// <summary>
/// S74-C：策略合法參數鍵清單的 Application 抽象 — 讓 Infrastructure 層的
/// <c>GlobalAiChatService</c> 能拿到 union expected keys 餵 <c>AiAdvicePayloadParser</c>，
/// 而不必反向引用 ConsoleApp 的 <c>StrategyCatalog</c>（違反 IRON ⑥ 四層相依）。
///
/// 具體實作在 ConsoleApp 端以 adapter 包裝 <c>StrategyCatalog</c>，DI 註冊 Singleton。
/// </summary>
public interface IStrategyParameterKeyCatalog
{
    /// <summary>
    /// 所有已註冊策略的 ExpectedParameterKeys 取 union（去重、保序）。
    /// GlobalAiSidebar 全域無 strategy context 時的預設過濾清單。
    /// </summary>
    IReadOnlyList<string> AllParameterKeys { get; }

    /// <summary>
    /// 指定 strategyKey 的 ExpectedParameterKeys（精準過濾用）；未知 key 回 null。
    /// AI 在 JSON payload 內帶 <c>"strategyKey"</c> 欄位時，sidebar 可二次以本方法精準過濾。
    /// </summary>
    IReadOnlyList<string>? KeysFor(string strategyKey);
}
