using CryptoBot.Domain.Aggregates.MarketDataAggregate;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 市場 regime 分類（CAP-008、對齊 reflection 缺角 #4 Regime Stratification）。
/// </summary>
public enum MarketRegime
{
    Bull,
    Range,
    Bear
}

/// <summary>
/// CAP-008：依「過去 N 根 K 線 close 平均對比當前 close」判定市場 regime。
/// 純價格邏輯、無外部 indicator 依賴；資料不足時保守回 <see cref="MarketRegime.Range"/>。
///
/// 跨幣種跨週期通用 — 用 % change + 絕對 threshold、不需 per-symbol tuning。
/// 對應 reflection 缺角 #4：「DOGE 是 meme coin、bull / range / bear 三市況 trend following
/// 表現差異 ≥ 3x；單時段回測掩蓋這個」— 本 classifier 為 backtesting framework 端的執行載體。
/// </summary>
public sealed class RegimeClassifier
{
    /// <summary>
    /// MA lookback bars。1H interval 對應 20 days × 24h = 480；其他週期語義為「最近 N 根」。
    /// </summary>
    public const int MaLookbackBars = 480;

    /// <summary>當前 close 比 MA 高 ≥ 5% → Bull。</summary>
    public const decimal BullThreshold = 0.05m;

    /// <summary>當前 close 比 MA 低 ≥ 5% → Bear。</summary>
    public const decimal BearThreshold = -0.05m;

    /// <summary>
    /// 依過去 <see cref="MaLookbackBars"/> 根 close 平均 vs 當前 close 判定 regime。
    /// 資料不足 → <see cref="MarketRegime.Range"/>（保守、不在 warmup 期亂分類）。
    /// </summary>
    public MarketRegime Classify(IReadOnlyList<Kline> historicalKlines)
    {
        if (historicalKlines.Count < MaLookbackBars)
            return MarketRegime.Range;

        var current = historicalKlines[^1].Close;
        decimal sum = 0m;
        var startIndex = historicalKlines.Count - MaLookbackBars;
        for (int i = startIndex; i < historicalKlines.Count; i++)
            sum += historicalKlines[i].Close;
        var ma = sum / MaLookbackBars;
        if (ma == 0m) return MarketRegime.Range;

        var pctChange = (current - ma) / ma;
        return pctChange >= BullThreshold ? MarketRegime.Bull
             : pctChange <= BearThreshold ? MarketRegime.Bear
             : MarketRegime.Range;
    }
}
