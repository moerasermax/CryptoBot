namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// S66-E：啟動 Pre-flight 檢查的設定。對應 <c>appsettings.json</c> 的 <c>Startup</c> section。
/// </summary>
public sealed class StartupOptions
{
    public const string SectionName = "Startup";

    /// <summary>
    /// 偏差絕對值超過此 ms 時，啟動程序立即 <c>Environment.Exit(1)</c>。
    /// <c>null</c>（預設）= 不阻擋（Demo / 開發環境）。
    /// 實盤建議設 <c>1000</c>（與 RiskManager 攔截閾值對齊）。
    /// </summary>
    public int? AbortIfSkewExceedsMs { get; set; }
}
