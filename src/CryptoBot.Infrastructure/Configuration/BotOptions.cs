namespace CryptoBot.Infrastructure.Configuration;

/// <summary>
/// BingX API 配置 - 從 appsettings.json 讀取
/// </summary>
public sealed class BingXOptions
{
    public const string SectionName = "BingX";

    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>是否使用模擬環境 (建議初期設為 true)</summary>
    public bool UseDemoTrading { get; set; } = true;

    /// <summary>請求超時 (秒)</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>WebSocket 重連延遲 (毫秒)</summary>
    public int WebSocketReconnectDelayMs { get; set; } = 3000;
}

/// <summary>
/// 機器人全域配置
/// </summary>
public sealed class BotOptions
{
    public const string SectionName = "Bot";

    /// <summary>當前啟用的交易對清單</summary>
    public List<string> ActiveSymbols { get; set; } = new() { "BTC-USDT" };

    /// <summary>K 線週期 (OneMinute, FiveMinutes, FifteenMinutes, OneHour, FourHours, OneDay)</summary>
    public string KlineInterval { get; set; } = "FifteenMinutes";

    /// <summary>槓桿倍數</summary>
    public int Leverage { get; set; } = 3;

    /// <summary>單筆風險百分比</summary>
    public decimal RiskPerTradePercent { get; set; } = 0.02m;

    /// <summary>止損百分比</summary>
    public decimal StopLossPercent { get; set; } = 0.02m;

    /// <summary>止盈百分比</summary>
    public decimal TakeProfitPercent { get; set; } = 0.04m;

    /// <summary>追蹤停損 (null 表示不啟用)</summary>
    public decimal? TrailingStopPercent { get; set; }

    /// <summary>啟用的策略清單</summary>
    public List<string> EnabledStrategies { get; set; } = new() { "TrendFollowing" };

    /// <summary>輪詢週期 (秒)</summary>
    public int TickIntervalSeconds { get; set; } = 60;
}

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";
    public string? WebhookUrl { get; set; }
    public bool Enabled { get; set; }
}
