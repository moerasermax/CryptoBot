using CryptoBot.Application.Indicators;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Strategies.MtfMeanReversion;

/// <summary>
/// S63 Phase 4：多週期均值回歸策略。
///
/// 開倉邏輯（OpenLong）：
///   1) 1H 定性：最近已收盤的 1H K 線收盤 ≤ 1H 布林帶下軌（超跌 / Oversold by Sigma）。
///   2) 15m 定點任一：
///        a. <see cref="PatternDetector.DetectRsiBullishDivergence"/> 偵測到底背離；或
///        b. 最近已收盤的 15m K 線是 Bullish Pin Bar，且其收盤站在 15m EMA20 之上。
///   3) 目前沒有本策略名下的持倉。
///
/// 平倉邏輯（CloseLong / 4H 管理最小實作）：
///   - 已持有本策略的 Long 部位，且當前 mark price 跌破最近已收盤的 4H EMA20 → 出場。
///   - 完整的「獲利脫離成本區後才切 4H 追蹤」需要持倉同步器升級，交由下一膠囊處理。
///
/// 參數（走 <see cref="StrategyConfiguration.Parameters"/>）：
///   - <c>Bb1hPeriod</c>       = 20
///   - <c>Bb1hStdDev</c>       = 2
///   - <c>Rsi15mPeriod</c>     = 14
///   - <c>Ema15mPeriod</c>     = 20
///   - <c>Ema4hPeriod</c>      = 20
///   - <c>DivergenceLookback</c> = 30
///   - <c>PivotSpan</c>        = 2
/// </summary>
public sealed class MtfMeanReversionStrategy : IMultiTimeframeStrategy
{
    public string StrategyType => "MtfMeanReversion";

    public IReadOnlyList<KlineInterval> RequiredIntervals { get; } = new[]
    {
        KlineInterval.OneHour,
        KlineInterval.FourHours,
    };

    /// <summary>
    /// 單週期路徑不可能驗證 1H / 4H 條件 — 被執行器誤呼叫時靜默回 None，避免產生假訊號。
    /// 實際的執行器會走 <see cref="AnalyzeMultiTimeframeAsync"/>。
    /// </summary>
    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
        => Task.FromResult(TradingSignal.None(config.Symbol, snapshot.FuturesMarkPrice));

    public Task<TradingSignal> AnalyzeMultiTimeframeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> primaryKlines,
        IReadOnlyDictionary<KlineInterval, IReadOnlyList<Kline>> additionalKlines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        var currentPrice = snapshot.FuturesMarkPrice;
        var none = TradingSignal.None(config.Symbol, currentPrice);

        // ----- 參數 -----
        var bb1hPeriod = (int)config.GetParameter("Bb1hPeriod", 20m);
        var bb1hStdDev = config.GetParameter("Bb1hStdDev", 2m);
        var rsi15mPeriod = (int)config.GetParameter("Rsi15mPeriod", 14m);
        var ema15mPeriod = (int)config.GetParameter("Ema15mPeriod", 20m);
        var ema4hPeriod = (int)config.GetParameter("Ema4hPeriod", 20m);
        var divergenceLookback = (int)config.GetParameter("DivergenceLookback", 30m);
        var pivotSpan = (int)config.GetParameter("PivotSpan", 2m);

        // ----- 額外週期數據 -----
        if (!additionalKlines.TryGetValue(KlineInterval.OneHour, out var klines1h) || klines1h.Count < bb1hPeriod + 2)
            return Task.FromResult(none);
        if (!additionalKlines.TryGetValue(KlineInterval.FourHours, out var klines4h) || klines4h.Count < ema4hPeriod + 2)
            return Task.FromResult(none);
        if (primaryKlines.Count < Math.Max(rsi15mPeriod, ema15mPeriod) + 5)
            return Task.FromResult(none);

        // ----- 平倉優先檢查（已有本策略 Long 部位）-----
        var openLong = openPositions.FirstOrDefault(p =>
            !p.IsClosed && p.Side == PositionSide.Long);
        if (openLong is not null)
        {
            // 用**已收盤**的最新 4H EMA20 作為出場濾波
            var ema4h = TechnicalIndicators.EMA(klines4h, ema4hPeriod);
            var ema4hLast = ema4h[^1];
            if (ema4hLast is decimal exitLevel && currentPrice.Value < exitLevel)
            {
                return Task.FromResult(TradingSignal.CloseLong(
                    config.Symbol, currentPrice,
                    $"MTF-MR exit: price {currentPrice.Value:F2} < 4H EMA{ema4hPeriod} {exitLevel:F2}"));
            }
            return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
        }

        // ----- 開倉檢查 -----

        // (1) 1H 定性：最近已收盤的 1H 收盤價是否觸及 BB 下軌
        //     Executor 已切除「進行中」尾根，所以 klines1h[^1] 是最新已收盤 K。
        var bb1h = TechnicalIndicators.CalculateBollingerBands(klines1h, bb1hPeriod, bb1hStdDev);
        var last1hIdx = klines1h.Count - 1;
        if (bb1h.Lower[last1hIdx] is not decimal lower1h) return Task.FromResult(none);
        if (klines1h[last1hIdx].Close > lower1h) return Task.FromResult(none);

        // (2) 15m 定點：RSI 底背離 OR Bullish Pin Bar 且收盤站上 15m EMA20
        var rsi15m = TechnicalIndicators.RSI(primaryKlines, rsi15mPeriod);
        var ema15m = TechnicalIndicators.EMA(primaryKlines, ema15mPeriod);

        bool hasDivergence = PatternDetector.DetectRsiBullishDivergence(
            primaryKlines, rsi15m, divergenceLookback, pivotSpan);

        // Pin Bar 看「最近已確認」K 線 — 主週期可能含進行中尾根，安全起見用倒數第 2 根
        bool hasPinBarBreakout = false;
        if (primaryKlines.Count >= 2)
        {
            var confirmIdx = primaryKlines.Count - 2;
            var confirmKline = primaryKlines[confirmIdx];
            if (PatternDetector.IsBullishPinBar(confirmKline)
                && ema15m[confirmIdx] is decimal ema15mVal
                && confirmKline.Close > ema15mVal)
            {
                hasPinBarBreakout = true;
            }
        }

        if (!hasDivergence && !hasPinBarBreakout)
            return Task.FromResult(none);

        // ----- 組裝訊號 -----
        var sl = Price.Create(currentPrice.Value * (1m - config.StopLossPercent));
        var tp = Price.Create(currentPrice.Value * (1m + config.TakeProfitPercent));

        var triggers = new List<string>();
        if (hasDivergence) triggers.Add("RSI-divergence");
        if (hasPinBarBreakout) triggers.Add("15m-PinBar>EMA20");

        // 信心：兩訊號同時觸發給最強 0.9，單一給 0.7
        var confidence = hasDivergence && hasPinBarBreakout ? 0.9m : 0.7m;

        return Task.FromResult(TradingSignal.OpenLong(
            config.Symbol, currentPrice, sl, tp, confidence,
            $"MTF-MR Long: 1H close {klines1h[last1hIdx].Close:F2} ≤ BB Lower {lower1h:F2}; " +
            $"15m [{string.Join(" + ", triggers)}]"));
    }
}
