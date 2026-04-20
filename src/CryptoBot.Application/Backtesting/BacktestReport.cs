using CryptoBot.Domain.Aggregates.OrderAggregate;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 單次回測的彙總結果 — 之後可進一步加 Sharpe / MaxDrawdown / WinRate 等指標。
/// </summary>
public sealed record BacktestReport(
    int TotalKlines,
    int SignalsTriggered,
    int OrdersFilled,
    decimal StartingBalance,
    decimal EndingBalance,
    decimal PeakEquity,
    decimal MaxDrawdownPercent,
    DateTime? FirstKlineTime,
    DateTime? LastKlineTime,
    IReadOnlyList<Order> Fills)
{
    public decimal NetPnL => EndingBalance - StartingBalance;
    public decimal ReturnPercent => StartingBalance == 0m ? 0m : NetPnL / StartingBalance * 100m;
}
