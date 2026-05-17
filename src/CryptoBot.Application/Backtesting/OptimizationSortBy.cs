namespace CryptoBot.Application.Backtesting;

/// <summary>
/// S77 P0-2：Top N 排行榜排序維度 — 預設 Sharpe（量化界最通用的風險調整報酬指標）。
///
/// 對齊 ClaudeDesktop 2026-05-17 bug report:
///   - 既有 default 用 ProfitToDrawdownRatio (P/DD)、未在 UI 標示
///   - 期望可選 + UI 明確標示「目前按 X 排序」
/// </summary>
public enum OptimizationSortBy
{
    /// <summary>Sharpe ratio (預設) — 量化界通用、單位風險超額報酬。</summary>
    Sharpe = 0,

    /// <summary>Profit to Drawdown ratio (P/DD) — 累積獲利除以最大回撤、強調 drawdown 控制。</summary>
    ProfitToDrawdownRatio = 1,

    /// <summary>Net PnL (絕對獲利)。</summary>
    NetPnL = 2,

    /// <summary>Return percent (相對報酬率)。</summary>
    ReturnPercent = 3,

    // Win rate (勝率) — TODO Phase 2: BacktestReport 需先加 WinRate 計算欄位 (從 Fills.RealizedPnL 算正/負比例)
}
