namespace CryptoBot.ConsoleApp.Api.Dtos;

/// <summary>
/// 策略 API 的輸出格式。把 Domain 的 Strategy + StrategyConfiguration 壓平，方便前端直接 bind，
/// 也避開 ValueObject（Symbol / Leverage）私有欄位在 JsonSerializer 下會變空物件的坑。
/// </summary>
public sealed record StrategyDto(
    Guid Id,
    string Name,
    string StrategyType,
    string Status,                  // "Running" / "Paused" / "Stopped" / "Error"
    string Symbol,
    string Interval,
    int Leverage,
    decimal RiskPerTradePercent,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int MaxKlineWindow,
    IReadOnlyDictionary<string, decimal> Parameters,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    decimal WinRate,
    decimal CumulativePnL,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? StoppedAt,
    string? LastError);

public sealed record ToggleResponseDto(Guid Id, string NewStatus, string Message);
