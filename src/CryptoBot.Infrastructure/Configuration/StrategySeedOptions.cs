namespace CryptoBot.Infrastructure.Configuration;

/// <summary>
/// 啟動時預置（seed）一筆策略到 DB。
///
/// S7 全線試車用：若 DB 中沒有同名策略，就依這份設定建立一筆、狀態直接設為 <c>Running</c>，
/// 讓 <c>StrategyRuntimeHostedService</c> 起來時就能載入並跑起來。
/// 已存在同名策略時不會重複建立，也不會覆蓋現有設定。
/// </summary>
public sealed class StrategySeedOptions
{
    public const string SectionName = "StrategySeed";

    /// <summary>是否啟用 seed。關閉時整個 seeder 略過。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>策略在 DB 中的唯一名稱（做存在性檢查的 key）。</summary>
    public string Name { get; set; } = "SMA-BTC15m-TestDrive";

    /// <summary>對應 <see cref="CryptoBot.Application.Strategies.IStrategy.StrategyType"/>。</summary>
    public string StrategyType { get; set; } = "SmaCrossover";

    public string Symbol { get; set; } = "BTC-USDT";

    /// <summary>如 "OneMinute" / "FifteenMinutes" / "OneHour" / "FourHours" / "OneDay"。</summary>
    public string KlineInterval { get; set; } = "FifteenMinutes";

    public int Leverage { get; set; } = 3;
    public decimal RiskPerTradePercent { get; set; } = 0.02m;
    public decimal StopLossPercent { get; set; } = 0.02m;
    public decimal TakeProfitPercent { get; set; } = 0.04m;

    /// <summary>K 線視窗上限 — 需 ≥ SlowSmaPeriod + 一些 buffer。</summary>
    public int MaxKlineWindow { get; set; } = 200;

    public int FastSmaPeriod { get; set; } = 20;
    public int SlowSmaPeriod { get; set; } = 50;

    /// <summary>建立完是否立刻 <c>Strategy.Start()</c>（狀態變 Running）。</summary>
    public bool StartImmediately { get; set; } = true;
}
