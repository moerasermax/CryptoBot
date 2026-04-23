using CryptoBot.Application.Backtesting;

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
///   <item><see cref="EquityCurve"/> — S25 T2：回測期間的權益序列（經下採樣至 ≤1000 點）。
///         為避免廣播 payload 過大，Orchestrator 只對 Rank 1 填值，其他列為空陣列。</item>
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
    int Fills,
    IReadOnlyList<EquityPoint> EquityCurve,
    decimal SharpeRatio,
    IReadOnlyList<FillMarkerDto> FillMarkers,
    bool IsLiquidated = false);

/// <summary>
/// S26 T3：權益曲線上的交易標註。
/// <list type="bullet">
///   <item><see cref="PositionSide"/> — "Long" / "Short"（字串序列化，前端直接判斷顏色）</item>
///   <item><see cref="Side"/> — "Buy" / "Sell"（開倉/平倉由 PositionSide × Side 組合判斷）</item>
///   <item><see cref="Price"/> — 成交均價（AverageFillPrice?.Value，沒有則 0）</item>
/// </list>
/// 只 Rank 1 的 row 會攜帶非空 markers，與 <c>EquityCurve</c> 同樣的 payload 節流策略。
/// </summary>
public sealed record FillMarkerDto(
    DateTime CreatedAt,
    string PositionSide,
    string Side,
    decimal Price,
    decimal Quantity);

public sealed record OptimizationCompletedUpdate(
    int TotalRuns,
    int Shown,
    int FilteredOut,
    IReadOnlyList<LeaderboardRowDto> Rows);

public sealed record OptimizationFailedUpdate(string Error);
