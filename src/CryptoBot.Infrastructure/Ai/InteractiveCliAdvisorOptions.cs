namespace CryptoBot.Infrastructure.Ai;

/// <summary>
/// S74：互動式本地 CLI Advisor（<see cref="InteractiveCliAdvisorService"/>）的配置。
///
/// 切換來源：<c>Configuration["AiAdvisor:Provider"] == "InteractiveCli"</c> 時 DI 註冊本實作；
/// 預設仍走 <see cref="GeminiAiAdvisorService"/>（HTTP REST）。
///
/// 預設值說明：
///   - <see cref="Executable"/>=<c>"gemini"</c>：npm 全域安裝後在 PATH。
///   - <see cref="ArgumentsTemplate"/>=<c>"-i \"{prompt}\""</c>：<c>{prompt}</c> 會被替換為跳脫後的提示詞。
///       若使用者本機 gemini CLI 不認 <c>-i</c>，可改成符合該版本的旗標（如 <c>chat \"{prompt}\"</c>）。
///   - <see cref="HistoryDirectory"/>=<c>"../../agent-commons/state/advisor_history"</c>：
///       相對於 ConsoleApp 工作目錄；governance migration 後 state 統一收於 agent-commons/。
///       絕對路徑亦支援。
///   - <see cref="TimeoutSeconds"/>=<c>600</c>：人工討論需要時間，10 分鐘為合理上限。
///   - <see cref="PollIntervalMs"/>=<c>1000</c>：每秒輪詢一次 advice 檔；CPU 成本可忽略。
/// </summary>
public sealed class InteractiveCliAdvisorOptions
{
    public const string SectionName = "AiAdvisor:InteractiveCli";

    /// <summary>本地 CLI 執行檔名稱或絕對路徑。</summary>
    public string Executable { get; set; } = "gemini";

    /// <summary>
    /// 傳給 CLI 的參數樣板。<c>{prompt}</c> 會被替換為跳脫後的 prompt 字串。
    /// 預設：<c>-i "{prompt}"</c>。
    /// </summary>
    public string ArgumentsTemplate { get; set; } = "-i \"{prompt}\"";

    /// <summary>
    /// 對話結果 JSON 的輸出目錄；相對路徑以 ConsoleApp 啟動時的工作目錄為基準。
    /// </summary>
    public string HistoryDirectory { get; set; } = "../../agent-commons/state/advisor_history";

    /// <summary>整個討論流程的逾時秒數；超過即放棄等候、回 Success=false。</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>輪詢 advice JSON 檔出現的間隔（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 1000;
}
