namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 單一參數組合的回測結果。參數以 (name, value) 字典存，方便未來擴充到多指標。
///
/// S77 P0-1 fix: 加 <see cref="TrialCount"/> 欄位 — Bayesian sampler 收斂後可能反覆 suggest 同 paramSet,
/// StrategyOptimizer 在 return 前 GroupBy paramSet hash + 保留 First + 計算重複次數,
/// 避免 Top N 排行榜出現多筆同 params 重複 row (ClaudeDesktop 2026-05-17 bug report)。
/// </summary>
/// <param name="Parameters">參數組合 (name, value) 字典</param>
/// <param name="Report">回測結果報告</param>
/// <param name="TrialCount">此 paramSet 在 optimization 內 trial 次數（dedup 前的計數，預設 1）</param>
public sealed record OptimizationRun(
    IReadOnlyDictionary<string, decimal> Parameters,
    BacktestReport Report,
    int TrialCount = 1)
{
    public decimal GetParameter(string name) => Parameters.TryGetValue(name, out var v) ? v : 0m;

    /// <summary>
    /// S77 P0-1: paramSet hash 用於 dedup — culture-invariant, ordered key, "Key=Value|..." format.
    /// </summary>
    public string ParamSetHash() =>
        string.Join("|", Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
}
