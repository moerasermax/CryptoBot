namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 單一參數組合的回測結果。參數以 (name, value) 字典存，方便未來擴充到多指標。
/// </summary>
public sealed record OptimizationRun(
    IReadOnlyDictionary<string, decimal> Parameters,
    BacktestReport Report)
{
    public decimal GetParameter(string name) => Parameters.TryGetValue(name, out var v) ? v : 0m;
}
