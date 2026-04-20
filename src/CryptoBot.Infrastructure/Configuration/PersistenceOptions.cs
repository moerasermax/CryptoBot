namespace CryptoBot.Infrastructure.Configuration;

/// <summary>
/// 持久層設定 — 從 appsettings.json "Persistence" 區段讀取。
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>
    /// EF Core 連線字串。預設使用應用程式工作目錄下的 <c>cryptobot.db</c>。
    /// 範例：<c>"Data Source=cryptobot.db"</c>、<c>"Data Source=C:/data/cryptobot.db"</c>
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=cryptobot.db";

    /// <summary>
    /// 是否在啟動時自動套用 migration（production 建議 true，dev 可 false 改走 `dotnet ef database update`）。
    /// </summary>
    public bool AutoMigrateOnStartup { get; set; } = false;
}
