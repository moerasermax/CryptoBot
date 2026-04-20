namespace CryptoBot.ConsoleApp.Realtime;

public sealed record OptimizationProgressUpdate(
    int Completed,
    int Total,
    string CurrentParams);

public sealed record LeaderboardRowDto(
    int Rank,
    int Fast,
    int Slow,
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
