using CryptoBot.Domain.Aggregates.MarketDataAggregate;

namespace CryptoBot.Application.Indicators;

/// <summary>
/// S63 Phase 2：價格結構與動量背離辨識。
/// 純函數、無狀態 — 與 <see cref="TechnicalIndicators"/> 同設計哲學。
///
/// 為了避免「未來函數」(lookahead bias)，所有 pivot 偵測都要求左右各 <c>pivotSpan</c> 根 K 線
/// 比候選點高（bullish pivot low）—— 亦即最末 <c>pivotSpan</c> 根永遠不會被認定為 pivot，
/// 直到新的 K 線進來確認。呼叫端若在「進行中」K 線上做決策，風險由呼叫端承擔。
/// </summary>
public static class PatternDetector
{
    /// <summary>
    /// 偵測 RSI 底背離（Bullish Divergence）：
    /// 在最近 <paramref name="lookback"/> 根 K 線內找最新兩個 pivot low，若價格破底但 RSI 抬高 → true。
    /// </summary>
    /// <param name="klines">K 線序列（舊→新）。</param>
    /// <param name="rsi">對應 klines 的 RSI 值（<see cref="TechnicalIndicators.RSI"/> 輸出）。</param>
    /// <param name="lookback">搜尋 pivot 的視窗長度（從尾端往回算）。</param>
    /// <param name="pivotSpan">pivot 左右比較根數；2 = 前兩根與後兩根都要比中心高。</param>
    public static bool DetectRsiBullishDivergence(
        IReadOnlyList<Kline> klines,
        IReadOnlyList<decimal?> rsi,
        int lookback = 30,
        int pivotSpan = 2)
    {
        if (klines is null || rsi is null) return false;
        if (pivotSpan < 1) pivotSpan = 1;
        if (klines.Count != rsi.Count) return false;
        if (klines.Count < pivotSpan * 2 + 2) return false;

        var pivots = FindPivotLows(klines, rsi, lookback, pivotSpan);
        if (pivots.Count < 2) return false;

        var p1 = pivots[^2];
        var p2 = pivots[^1];

        // 確保兩個 pivot 有時序：新 pivot 在較舊 pivot 之後。
        if (p2.Index <= p1.Index) return false;

        // 背離條件：price breaks lower（新點的 Low < 舊點的 Low），但 RSI 抬高（新 RSI > 舊 RSI）。
        // 兩個條件必須嚴格不等，避免平坦無訊號被當成訊號。
        var priceBreaksLower = klines[p2.Index].Low < klines[p1.Index].Low;
        var rsiHigherLow = p2.Rsi > p1.Rsi;
        return priceBreaksLower && rsiHigherLow;
    }

    /// <summary>
    /// 在 <paramref name="lookback"/> 視窗內尋找所有已確認（非進行中）的 pivot low。
    /// 尾端 <paramref name="pivotSpan"/> 根刻意排除 —「未來」方向還沒確定。
    /// </summary>
    private static List<Pivot> FindPivotLows(
        IReadOnlyList<Kline> klines,
        IReadOnlyList<decimal?> rsi,
        int lookback,
        int pivotSpan)
    {
        var result = new List<Pivot>();

        int n = klines.Count;
        int from = Math.Max(pivotSpan, n - lookback);
        int to = n - 1 - pivotSpan;  // 右側要有 pivotSpan 根「未來」K 線才能確認

        for (int i = from; i <= to; i++)
        {
            if (rsi[i] is null) continue;

            var centerLow = klines[i].Low;
            bool isPivot = true;
            for (int k = 1; k <= pivotSpan; k++)
            {
                // 嚴格小於：避免平坦區段被判成 pivot
                if (klines[i - k].Low <= centerLow || klines[i + k].Low <= centerLow)
                {
                    isPivot = false;
                    break;
                }
            }
            if (isPivot)
                result.Add(new Pivot(i, klines[i].Low, rsi[i]!.Value));
        }

        return result;
    }

    /// <summary>
    /// 進階 Bullish Pin Bar：強下影、弱上影、小實體、不要求 bullish close（允許 doji 型變體）。
    /// 判定標準：
    ///   (1) LowerShadow ≥ 2 × BodySize（實體小）
    ///   (2) LowerShadow ≥ 0.66 × Range（下影佔 2/3）
    ///   (3) UpperShadow ≤ 0.25 × Range（上影微小）
    /// </summary>
    public static bool IsBullishPinBar(Kline k)
    {
        if (k is null) return false;
        if (k.Range <= 0m) return false;

        var range = k.Range;
        // Doji：實體為 0 時只驗下影/上影比例即可
        if (k.BodySize == 0m)
            return k.LowerShadow >= 0.66m * range && k.UpperShadow <= 0.25m * range;

        return k.LowerShadow >= 2m * k.BodySize
            && k.LowerShadow >= 0.66m * range
            && k.UpperShadow <= 0.25m * range;
    }

    /// <summary>
    /// 進階 Bearish Pin Bar：鏡像版。
    /// </summary>
    public static bool IsBearishPinBar(Kline k)
    {
        if (k is null) return false;
        if (k.Range <= 0m) return false;

        var range = k.Range;
        if (k.BodySize == 0m)
            return k.UpperShadow >= 0.66m * range && k.LowerShadow <= 0.25m * range;

        return k.UpperShadow >= 2m * k.BodySize
            && k.UpperShadow >= 0.66m * range
            && k.LowerShadow <= 0.25m * range;
    }

    private readonly record struct Pivot(int Index, decimal Low, decimal Rsi);
}
