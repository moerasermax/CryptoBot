namespace CryptoBot.ConsoleApp.Realtime;

public sealed record OptimizationProgressUpdate(
    int Completed,
    int Total,
    string CurrentParams);

/// <summary>
/// Leaderboard 一列（前端顯示 + 套用用）。
///
/// <list type="bullet">
///   <item><see cref="Parameters"/> — 整包該策略的參數字典，直接灌進 StrategyConfiguration。</item>
///   <item><see cref="ParameterSummary"/> — 人類友善字串，例如
///         "Fast=5 / Slow=30"（SMA）或 "RSI=14 (30/70) · BB=20±2.0"（B46）。</item>
/// </list>
/// 統一為字典後，新策略上線時不需要再改此 DTO 或 UI 欄位結構。
/// </summary>
public sealed record LeaderboardRowDto(
    int Rank,
    IReadOnlyDictionary<string, decimal> Parameters,
    string ParameterSummary,
    decimal NetPnL,
    decimal ReturnPercent,
    decimal MaxDrawdownPercent,
    decimal ProfitToDrawdownRatio,
    bool IsInfiniteRatio,
    int Fills);

public sealed record OptimizationCompletedUpdate(
    int TotalRuns,
    int Shown,
    int FilteredOut,
    IReadOnlyList<LeaderboardRowDto> Rows);

public sealed record OptimizationFailedUpdate(string Error);
