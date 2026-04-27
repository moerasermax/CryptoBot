namespace CryptoBot.Application.Realtime;

/// <summary>
/// 儀表板頂部三卡的即時推播負載（S70 後拆分版本）。
/// <list type="bullet">
///   <item><see cref="TotalEquity"/> = 合約 quote 餘額 + <see cref="OpenUnrealizedPnL"/>。</item>
///   <item><see cref="TodayRealizedPnL"/> = 依使用者本地時區「今日 00:00」起，所有
///         已平倉 Position 的 RealizedPnL（已扣手續費）總和。對應使用者直覺的「今日落袋多少」。</item>
///   <item><see cref="OpenUnrealizedPnL"/> = 當前所有開倉 Position 的 UnrealizedPnL 加總，
///         不分今日昨日，純粹是「目前帳面浮多少」。S70 前舊版誤把這個值與已實現相加成一個
///         合併欄位，導致顯示語意與真相錯位。</item>
///   <item><see cref="ActiveStrategyCount"/> = <c>StrategyStatus.Running</c> 的策略數。</item>
/// </list>
/// </summary>
public sealed record DashboardStatsUpdate(
    DateTime Timestamp,
    decimal TotalEquity,
    decimal TodayRealizedPnL,
    decimal OpenUnrealizedPnL,
    int ActiveStrategyCount);
