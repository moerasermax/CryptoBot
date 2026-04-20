namespace CryptoBot.Infrastructure.Configuration;

/// <summary>
/// 全域交易模式 — 唯一決定「用真錢還是模擬金」的開關。
/// Demo 必須對應 BingX 的 VST 模擬資產，Live 才是真 USDT。
/// </summary>
public enum TradingMode
{
    Demo = 0,
    Live = 1,
}

/// <summary>
/// BingX API 配置 - 從 appsettings.json 讀取
/// </summary>
public sealed class BingXOptions
{
    public const string SectionName = "BingX";

    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// 全域交易模式。預設 Demo（VST），永遠不會在沒被明確改成 Live 之前動到真錢。
    /// </summary>
    public TradingMode TradingMode { get; set; } = TradingMode.Demo;

    /// <summary>
    /// 舊欄位 — 仍從 config 讀，用來相容遺留的 appsettings.json。
    /// 解析優先順序：若 <see cref="TradingMode"/> 顯式被設成 Live → Live；
    /// 否則回到 <see cref="UseDemoTrading"/>（true → Demo, false → Live）。
    /// 兩個都沒給就走預設 Demo（最安全）。
    /// </summary>
    public bool UseDemoTrading { get; set; } = true;

    /// <summary>請求超時 (秒)</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>WebSocket 重連延遲 (毫秒)</summary>
    public int WebSocketReconnectDelayMs { get; set; } = 3000;

    /// <summary>
    /// 解析後的最終模式。新程式碼一律讀這個，**不要直接讀 <see cref="TradingMode"/> 或 <see cref="UseDemoTrading"/>**。
    /// </summary>
    public TradingMode EffectiveMode =>
        TradingMode == TradingMode.Live || !UseDemoTrading
            ? TradingMode.Live
            : TradingMode.Demo;

    /// <summary>
    /// 此模式對應的合約 quote 資產：Demo→"VST", Live→"USDT"。
    /// 任何「查合約餘額」的呼叫都應該帶這個值，避免在 demo 模式下查不到 USDT 而誤報 0。
    /// </summary>
    public string QuoteAsset =>
        EffectiveMode == TradingMode.Live ? "USDT" : "VST";
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
