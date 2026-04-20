namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 單一策略參數的掃描範圍：以 [Min, Max] 為區間，按 Step 線性展開。
/// 例：FastSmaPeriod 5→15 step=1 → {5,6,7,...,15} 共 11 個值。
/// </summary>
public sealed record ParameterRange(string Name, decimal Min, decimal Max, decimal Step)
{
    public IEnumerable<decimal> Enumerate()
    {
        if (Step <= 0m) throw new ArgumentException($"Step must be positive: {Step}", nameof(Step));
        if (Max < Min) throw new ArgumentException($"Max {Max} must be >= Min {Min}", nameof(Max));

        // 為避免浮點累積誤差，用整數 tick 次數迴圈，再倒回 decimal。
        var ticks = (int)Math.Floor((double)((Max - Min) / Step));
        for (var i = 0; i <= ticks; i++)
            yield return Min + Step * i;
    }
}
