namespace CryptoBot.ConsoleApp.Api.Dtos;

/// <summary>儀表板三卡 + 開倉清單的 API 回傳格式。</summary>
public sealed record DashboardStatsDto(
    decimal TotalEquity,
    decimal TodayPnL,
    int ActiveStrategyCount,
    IReadOnlyList<OpenPositionDto> OpenPositions,
    IReadOnlyList<RecentTradeDto> RecentTrades);

public sealed record OpenPositionDto(
    Guid Id,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal? CurrentPrice,
    decimal UnrealizedPnL,
    decimal UnrealizedPnLPercent,
    DateTime OpenedAt);

public sealed record RecentTradeDto(
    Guid OrderId,
    DateTime CreatedAt,
    string Symbol,
    string Side,
    string PositionSide,
    decimal Quantity,
    decimal? AverageFillPrice,
    string Status);
