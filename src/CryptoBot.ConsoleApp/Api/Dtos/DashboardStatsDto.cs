namespace CryptoBot.ConsoleApp.Api.Dtos;

/// <summary>儀表板三卡 + 開倉清單的 API 回傳格式（S70 後拆分版本）。</summary>
public sealed record DashboardStatsDto(
    decimal TotalEquity,
    decimal TodayRealizedPnL,
    decimal OpenUnrealizedPnL,
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
    string Status,
    string? RejectReason,
    string? ExchangeOrderId);

/// <summary>
/// S39：交易歷史列表 row — 對應「開倉 → 平倉」的一筆完整紀錄快照。
/// <para>
/// Entry/Exit 時間與價格、RealizedPnL、TotalCommission、StrategyType 字串、
/// ParametersSnapshot 都取自 Position 自身欄位，保證跨次啟動、跨 Strategy 改名都可還原。
/// </para>
/// </summary>
public sealed record ClosedTradeDto(
    Guid PositionId,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal EntryPrice,
    DateTime EntryTimeUtc,
    decimal? ExitPrice,
    DateTime? ExitTimeUtc,
    decimal RealizedPnL,
    decimal TotalCommission,
    int LeverageValue,
    string? StrategyType,
    string? ParametersSnapshot);
