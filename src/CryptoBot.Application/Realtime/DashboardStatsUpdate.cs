namespace CryptoBot.Application.Realtime;

/// <summary>
/// 儀表板頂部三卡的即時推播負載。
/// <list type="bullet">
///   <item><see cref="TotalEquity"/> = USDT 餘額 + 所有開倉的 UnrealizedPnL（mark-to-market）。</item>
///   <item><see cref="TodayPnL"/>   = 今日 UTC 00:00 以後 <c>Position.Close</c> 的 RealizedPnL 總和。</item>
///   <item><see cref="ActiveStrategyCount"/> = <c>StrategyStatus.Running</c> 的策略數。</item>
/// </list>
/// </summary>
public sealed record DashboardStatsUpdate(
    DateTime Timestamp,
    decimal TotalEquity,
    decimal TodayPnL,
    int ActiveStrategyCount);
