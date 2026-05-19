using CryptoBot.Domain.Aggregates.OrderAggregate;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 單次回測的彙總結果 — 之後可進一步加 Sharpe / MaxDrawdown / WinRate 等指標。
///
/// S25 T2：加入 <see cref="EquityCurve"/> — 回測期間的權益序列（mark-to-market equity vs 時間戳），
/// 由 BacktestEngine 依期望 K 線數動態下採樣至 ≤ <see cref="Backtesting.BacktestEngine.MaxEquityPoints"/> 點，
/// 避免長週期回測記憶體爆炸。
/// </summary>
/// <summary>
/// CAP-008：per-regime stats — Fills / Return % / MaxDrawdown % / Sharpe。
/// 三段（Bull / Range / Bear）由 <see cref="Backtesting.RegimeClassifier"/> 在 K 線跑過程中
/// 累積、由 <c>BacktestEngine</c> 在進場時 stamp regime 並於平倉時計入該 regime tracker。
/// </summary>
public sealed record RegimeBreakdown(
    int Fills,
    decimal Return,           // %
    decimal MaxDrawdown,      // %
    decimal SharpeRatio);

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
    IReadOnlyList<Order> Fills,
    IReadOnlyList<EquityPoint> EquityCurve,
    bool IsLiquidated = false,
    RegimeBreakdown? BullStats = null,
    RegimeBreakdown? RangeStats = null,
    RegimeBreakdown? BearStats = null)
{
    public decimal NetPnL => EndingBalance - StartingBalance;

    /// <summary>
    /// S32-T2：爆倉時一律回報 -100%，不論 EndingBalance 是否已被 Simulator 歸零（理論上必為 0，
    /// 但這裡保留強制值防呆），讓下游排行榜與摘要顯示一致。
    /// </summary>
    public decimal ReturnPercent => IsLiquidated
        ? -100m
        : (StartingBalance == 0m ? 0m : NetPnL / StartingBalance * 100m);

    /// <summary>
    /// S26 T1：年化夏普比率 — 以 <see cref="EquityCurve"/> 相鄰兩點的報酬率序列計算（無風險利率 = 0）。
    ///
    /// 計算：
    /// <list type="number">
    ///   <item>曲線 &lt; 2 點或全部權益 ≤ 0 → 0</item>
    ///   <item>每段 return_i = (equity_{i+1} - equity_i) / equity_i</item>
    ///   <item>若 stddev == 0（收益恆定，包含恆 0）→ 0，避免 0/0</item>
    ///   <item>年化係數：依曲線實際時間跨度推出「每年幾個 bar」再開根號，
    ///         這樣即使 EquityCurve 被 BacktestEngine 下採樣，算出的 Sharpe 仍是年化可比較值</item>
    /// </list>
    /// 使用 <c>double</c> 中介計算 — 統計運算 decimal 沒有 Sqrt 且速度劣勢明顯，最後只把結果轉回 decimal。
    /// </summary>
    public decimal SharpeRatio
    {
        get
        {
            if (EquityCurve.Count < 2) return 0m;

            // 收集相鄰報酬率。起點權益為 0（或負）時視為資料不可用 — 略過該段。
            var returns = new List<double>(EquityCurve.Count - 1);
            for (int i = 1; i < EquityCurve.Count; i++)
            {
                var prev = EquityCurve[i - 1].Equity;
                if (prev <= 0m) continue;
                var r = (double)((EquityCurve[i].Equity - prev) / prev);
                returns.Add(r);
            }
            if (returns.Count < 2) return 0m;

            double mean = 0;
            foreach (var r in returns) mean += r;
            mean /= returns.Count;

            double sumSq = 0;
            foreach (var r in returns) { var d = r - mean; sumSq += d * d; }
            // 樣本標準差（n-1）— 與常見金融套件慣例一致。
            var variance = sumSq / (returns.Count - 1);
            if (variance <= 0) return 0m;
            var stdDev = Math.Sqrt(variance);

            // 年化：由曲線首尾時間與點數推出「每年 bar 數」。
            // 若時間資訊缺失 / 跨度 ≤ 0，退化為「不年化」（因子 1），仍產出有意義的 Sharpe（只是不可跨週期比較）。
            double annualizationFactor = 1.0;
            if (FirstKlineTime is DateTime first
                && LastKlineTime is DateTime last
                && last > first)
            {
                var span = last - first;
                var secondsPerBar = span.TotalSeconds / (EquityCurve.Count - 1);
                if (secondsPerBar > 0)
                {
                    const double secondsPerYear = 365.25 * 24 * 3600;
                    var barsPerYear = secondsPerYear / secondsPerBar;
                    annualizationFactor = Math.Sqrt(barsPerYear);
                }
            }

            var sharpe = (mean / stdDev) * annualizationFactor;
            if (double.IsNaN(sharpe) || double.IsInfinity(sharpe)) return 0m;
            return (decimal)Math.Round(sharpe, 4);
        }
    }
}

/// <summary>
/// 權益曲線單點 — 某根 K 線收盤時 mark-to-market 的總權益。
/// <see cref="TimeUtc"/> 對齊該根 K 線 OpenTime (UTC)，UI 可直接當 x 軸。
/// </summary>
public sealed record EquityPoint(DateTime TimeUtc, decimal Equity);
