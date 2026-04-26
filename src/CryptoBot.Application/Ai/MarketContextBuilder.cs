using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Indicators;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Ai;

/// <summary>
/// 從交易所抓最近 N 根 K 線、跑 RSI/ATR/BB/EMA 得出 <see cref="MarketContext"/>。
/// 純邏輯，放 Application 層 — Infrastructure 只負責抓 K 線（透過 <see cref="IExchangeClient"/>）。
/// </summary>
public sealed class MarketContextBuilder : IMarketContextBuilder
{
    private readonly IExchangeClient _exchange;

    public MarketContextBuilder(IExchangeClient exchange)
    {
        _exchange = exchange;
    }

    public async Task<MarketContext> BuildAsync(
        Symbol symbol, KlineInterval interval, int klineCount = 100, CancellationToken ct = default)
    {
        if (klineCount < 30) klineCount = 30;
        if (klineCount > 500) klineCount = 500;

        var klines = await _exchange.GetKlinesAsync(symbol, interval, klineCount, ct: ct)
            .ConfigureAwait(false);

        if (klines.Count == 0)
        {
            return new MarketContext(
                Symbol: symbol.BingXFormat,
                Interval: interval,
                KlineCount: 0,
                LatestClose: 0m,
                Rsi14: null, Atr14: null,
                BbUpper: null, BbMiddle: null, BbLower: null,
                Ema20: null, Ema50: null,
                HighRecent: 0m, LowRecent: 0m,
                PercentChange: 0m,
                TrendLabel: TrendLabel.Unknown,
                BbPositionPercent: null);
        }

        var rsi = TechnicalIndicators.RSI(klines, 14);
        var atr = TechnicalIndicators.ATR(klines, 14);
        var bb  = TechnicalIndicators.CalculateBollingerBands(klines, 20, 2m);
        var ema20 = TechnicalIndicators.EMA(klines, 20);
        var ema50 = TechnicalIndicators.EMA(klines, 50);

        int last = klines.Count - 1;
        var latestClose = klines[last].Close;

        var rsiLast   = rsi[last];
        var atrLast   = atr[last];
        var bbUpLast  = bb.Upper[last];
        var bbMidLast = bb.Middle[last];
        var bbLoLast  = bb.Lower[last];
        var ema20Last = ema20[last];
        var ema50Last = ema50[last];

        // 最近 20 根的 H/L 給 AI 看「近期突破 vs 盤整」範圍
        int windowStart = Math.Max(0, klines.Count - 20);
        decimal highRecent = decimal.MinValue, lowRecent = decimal.MaxValue;
        for (int i = windowStart; i < klines.Count; i++)
        {
            if (klines[i].High > highRecent) highRecent = klines[i].High;
            if (klines[i].Low  < lowRecent)  lowRecent  = klines[i].Low;
        }

        // 視窗頭尾漲跌幅（純收盤對收盤）
        var firstClose = klines[0].Close;
        var percentChange = firstClose == 0 ? 0m
            : (latestClose - firstClose) / firstClose * 100m;

        var trend = ClassifyTrend(
            ema20Last, ema50Last, bbUpLast, bbLoLast, bbMidLast, latestClose, percentChange);

        decimal? bbPosition = null;
        if (bbUpLast.HasValue && bbLoLast.HasValue && bbUpLast.Value > bbLoLast.Value)
        {
            bbPosition = (latestClose - bbLoLast.Value) / (bbUpLast.Value - bbLoLast.Value);
        }

        return new MarketContext(
            Symbol: symbol.BingXFormat,
            Interval: interval,
            KlineCount: klines.Count,
            LatestClose: latestClose,
            Rsi14: rsiLast,
            Atr14: atrLast,
            BbUpper: bbUpLast,
            BbMiddle: bbMidLast,
            BbLower: bbLoLast,
            Ema20: ema20Last,
            Ema50: ema50Last,
            HighRecent: highRecent,
            LowRecent: lowRecent,
            PercentChange: percentChange,
            TrendLabel: trend,
            BbPositionPercent: bbPosition);
    }

    /// <summary>
    /// 簡單規則：EMA20 明顯離 EMA50 + 大漲/大跌 → 方向性趨勢；
    /// 否則若 BB 寬度相對中軌 &lt; 3% 視為收斂盤整。
    /// 這層只是給 Prompt 一個先驗 hint，AI 仍可依 RSI/BB 值覆寫判斷。
    /// </summary>
    private static TrendLabel ClassifyTrend(
        decimal? ema20, decimal? ema50,
        decimal? bbUpper, decimal? bbLower, decimal? bbMiddle,
        decimal latestClose, decimal percentChange)
    {
        if (ema20 is null || ema50 is null) return TrendLabel.Unknown;

        var slopeSignal = ema20.Value - ema50.Value;
        var meaningful = Math.Abs(slopeSignal) / (ema50.Value == 0 ? 1m : ema50.Value) > 0.005m;

        if (meaningful)
        {
            if (slopeSignal > 0 && percentChange > 0) return TrendLabel.Uptrend;
            if (slopeSignal < 0 && percentChange < 0) return TrendLabel.Downtrend;
        }

        if (bbUpper.HasValue && bbLower.HasValue && bbMiddle.HasValue && bbMiddle.Value > 0)
        {
            var width = (bbUpper.Value - bbLower.Value) / bbMiddle.Value;
            if (width < 0.03m) return TrendLabel.Ranging;
        }

        return TrendLabel.Ranging;
    }
}
