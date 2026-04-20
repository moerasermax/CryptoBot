using CryptoBot.Domain.Aggregates.MarketDataAggregate;

namespace CryptoBot.Application.Indicators;

/// <summary>
/// 技術指標計算引擎 - 純函數式，無狀態
/// 
/// 設計哲學：
/// - 所有方法為 static pure function
/// - 輸入 IReadOnlyList Kline, 輸出 IReadOnlyList decimal 或指標結果
/// - 不足數據的位置填 null, 讓呼叫方決定如何處理
/// </summary>
public static class TechnicalIndicators
{
    /// <summary>
    /// 簡單移動平均 (Simple Moving Average)
    /// </summary>
    public static IReadOnlyList<decimal?> SMA(IReadOnlyList<Kline> klines, int period)
    {
        var result = new decimal?[klines.Count];
        if (period <= 0 || klines.Count < period)
            return result;

        for (int i = period - 1; i < klines.Count; i++)
        {
            decimal sum = 0;
            for (int j = i - period + 1; j <= i; j++)
                sum += klines[j].Close;
            result[i] = sum / period;
        }
        return result;
    }

    /// <summary>
    /// 指數移動平均 (Exponential Moving Average)
    /// EMA = (close * multiplier) + (prev_ema * (1 - multiplier))
    /// multiplier = 2 / (period + 1)
    /// </summary>
    public static IReadOnlyList<decimal?> EMA(IReadOnlyList<Kline> klines, int period)
    {
        var result = new decimal?[klines.Count];
        if (period <= 0 || klines.Count < period) return result;

        var multiplier = 2m / (period + 1);

        // 初始 EMA 用 SMA
        decimal sum = 0;
        for (int i = 0; i < period; i++) sum += klines[i].Close;
        result[period - 1] = sum / period;

        for (int i = period; i < klines.Count; i++)
        {
            result[i] = klines[i].Close * multiplier
                      + result[i - 1]!.Value * (1 - multiplier);
        }
        return result;
    }

    /// <summary>
    /// RSI (Relative Strength Index) - 相對強弱指標
    /// 使用 Wilder's smoothing
    /// </summary>
    public static IReadOnlyList<decimal?> RSI(IReadOnlyList<Kline> klines, int period = 14)
    {
        var result = new decimal?[klines.Count];
        if (klines.Count <= period) return result;

        decimal gainSum = 0, lossSum = 0;
        for (int i = 1; i <= period; i++)
        {
            var change = klines[i].Close - klines[i - 1].Close;
            if (change > 0) gainSum += change;
            else lossSum -= change;
        }

        var avgGain = gainSum / period;
        var avgLoss = lossSum / period;
        result[period] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);

        for (int i = period + 1; i < klines.Count; i++)
        {
            var change = klines[i].Close - klines[i - 1].Close;
            var gain = change > 0 ? change : 0;
            var loss = change < 0 ? -change : 0;

            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;

            result[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
        }
        return result;
    }

    /// <summary>
    /// 布林帶 (Bollinger Bands)
    /// </summary>
    public static BollingerBands CalculateBollingerBands(
        IReadOnlyList<Kline> klines, int period = 20, decimal stdDevMultiplier = 2m)
    {
        var upper = new decimal?[klines.Count];
        var middle = SMA(klines, period);
        var lower = new decimal?[klines.Count];

        for (int i = period - 1; i < klines.Count; i++)
        {
            if (middle[i] is null) continue;
            var mean = middle[i]!.Value;

            decimal sumSq = 0;
            for (int j = i - period + 1; j <= i; j++)
            {
                var diff = klines[j].Close - mean;
                sumSq += diff * diff;
            }
            var stdDev = (decimal)Math.Sqrt((double)(sumSq / period));
            upper[i] = mean + stdDevMultiplier * stdDev;
            lower[i] = mean - stdDevMultiplier * stdDev;
        }

        return new BollingerBands(upper, middle, lower);
    }

    /// <summary>
    /// MACD (Moving Average Convergence Divergence)
    /// </summary>
    public static MacdResult CalculateMACD(
        IReadOnlyList<Kline> klines,
        int fastPeriod = 12,
        int slowPeriod = 26,
        int signalPeriod = 9)
    {
        var emaFast = EMA(klines, fastPeriod);
        var emaSlow = EMA(klines, slowPeriod);

        var macdLine = new decimal?[klines.Count];
        for (int i = 0; i < klines.Count; i++)
        {
            if (emaFast[i] is null || emaSlow[i] is null) continue;
            macdLine[i] = emaFast[i]!.Value - emaSlow[i]!.Value;
        }

        var signalLine = EmaOfSeries(macdLine, signalPeriod);
        var histogram = new decimal?[klines.Count];
        for (int i = 0; i < klines.Count; i++)
        {
            if (macdLine[i] is null || signalLine[i] is null) continue;
            histogram[i] = macdLine[i]!.Value - signalLine[i]!.Value;
        }
        return new MacdResult(macdLine, signalLine, histogram);
    }

    /// <summary>
    /// ATR (Average True Range) - 平均真實波幅 (用於動態止損)
    /// </summary>
    public static IReadOnlyList<decimal?> ATR(IReadOnlyList<Kline> klines, int period = 14)
    {
        var result = new decimal?[klines.Count];
        if (klines.Count <= period) return result;

        var trueRanges = new decimal[klines.Count];
        trueRanges[0] = klines[0].High - klines[0].Low;
        for (int i = 1; i < klines.Count; i++)
        {
            var tr1 = klines[i].High - klines[i].Low;
            var tr2 = Math.Abs(klines[i].High - klines[i - 1].Close);
            var tr3 = Math.Abs(klines[i].Low - klines[i - 1].Close);
            trueRanges[i] = Math.Max(tr1, Math.Max(tr2, tr3));
        }

        decimal sum = 0;
        for (int i = 0; i < period; i++) sum += trueRanges[i];
        result[period - 1] = sum / period;

        // Wilder's smoothing
        for (int i = period; i < klines.Count; i++)
            result[i] = (result[i - 1]!.Value * (period - 1) + trueRanges[i]) / period;

        return result;
    }

    // 輔助: 對一般 series 計算 EMA
    private static IReadOnlyList<decimal?> EmaOfSeries(IReadOnlyList<decimal?> series, int period)
    {
        var result = new decimal?[series.Count];
        var multiplier = 2m / (period + 1);
        int firstIdx = -1;
        for (int i = 0; i < series.Count; i++)
            if (series[i] is not null) { firstIdx = i; break; }
        if (firstIdx < 0 || firstIdx + period > series.Count) return result;

        decimal sum = 0;
        for (int i = firstIdx; i < firstIdx + period; i++) sum += series[i]!.Value;
        result[firstIdx + period - 1] = sum / period;

        for (int i = firstIdx + period; i < series.Count; i++)
        {
            if (series[i] is null) continue;
            result[i] = series[i]!.Value * multiplier
                      + result[i - 1]!.Value * (1 - multiplier);
        }
        return result;
    }
}

public sealed record BollingerBands(
    IReadOnlyList<decimal?> Upper,
    IReadOnlyList<decimal?> Middle,
    IReadOnlyList<decimal?> Lower);

public sealed record MacdResult(
    IReadOnlyList<decimal?> Macd,
    IReadOnlyList<decimal?> Signal,
    IReadOnlyList<decimal?> Histogram);
