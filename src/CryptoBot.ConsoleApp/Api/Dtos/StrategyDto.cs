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
    string? LastError,
    // S42 T1：上一次 AnalyzeAsync 的 UTC 時間。未執行過 / 已停機時為 null —
    // Dashboard 畫「Last Evaluated: [HH:MM:SS] (Ns ago)」用；後續變化由 WS StrategyEvaluated 事件推。
    DateTime? LastEvaluatedAtUtc);

public sealed record ToggleResponseDto(Guid Id, string NewStatus, string Message);

/// <summary>S25：把策略的「決策大腦」換一顆的請求 body。</summary>
public sealed record ChangeStrategyTypeRequest(string StrategyType);

public sealed record ChangeStrategyTypeResponseDto(
    Guid Id,
    string StrategyType,
    string Status,
    string Message);

/// <summary>
/// S28 T1：GET /api/strategies/breaker 回傳熔斷狀態。
/// UI 用 IsTripped 決定是否禁用 Start toggle 與顯示紫色 banner。
/// </summary>
public sealed record BreakerStatusDto(
    bool IsTripped,
    DateTime? TrippedAtUtc,
    string? Reason);

/// <summary>
/// S28 T1：POST /api/strategies/breaker/reset 回傳是否真的解除了（已非 tripped → false）。
/// </summary>
public sealed record BreakerResetResponseDto(
    bool Reset,
    string Message);

/// <summary>
/// S28 T2：POST /api/strategies/kill-switch 回傳執行摘要。
/// </summary>
public sealed record KillSwitchResponseDto(
    int OrdersCancelled,
    int PositionsClosed,
    int StrategiesStopped,
    IReadOnlyList<string> Errors,
    string Message);
