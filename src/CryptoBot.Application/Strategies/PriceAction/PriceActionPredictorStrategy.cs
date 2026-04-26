using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Strategies.PriceAction;

/// <summary>
/// S43 價格行為預測模型（Price Action Predictor）。
///
/// 與其他策略不同，此策略不依賴移動平均或振盪指標，而是從 K 線本身的
/// <c>OHLC</c> 結構推斷短期方向 — 符合「裸 K 交易」的直覺：
/// <list type="bullet">
///   <item><b>吞噬形態</b>（Engulfing）— 前一根陰 K 被當前陽 K 整根包住 ⇒ 看多；反之看空。</item>
///   <item><b>釘子棒 / 流星線</b>（Hammer / Shooting Star）— 影線長度對實體超過
///     <c>WickToBodyRatio</c> 倍、且方向一致 ⇒ 代表多/空強方有吞沒動作。</item>
///   <item><b>動能得分</b>（Momentum Score）— 最近 <c>LookbackPeriod</c> 根 K 的
///     <c>(Close - Open) / Close</c> 加總。超過 <c>MomentumThreshold</c> 才放行進場，
///     避免在無方向盤整中被形態假訊號騙進。</item>
/// </list>
///
/// <b>出場智慧</b>（T4 要求）：不光靠固定 SL/TP，持倉期間若見到反向形態（看多單遇到
/// Bearish Engulfing / Shooting Star；看空單遇到 Bullish Engulfing / Hammer），
/// 大腦主動吐出 <see cref="SignalType.CloseLong"/> / <see cref="SignalType.CloseShort"/>
/// 讓 executor 平倉，規避由停損價拉走才結束的被動虧損。
///
/// 參數（<see cref="StrategyConfiguration.GetParameter"/> 讀取）：
/// - <c>LookbackPeriod</c>（default=20）：動能窗口 K 數。
/// - <c>MomentumThreshold</c>（default=0.01）：動能絕對值至少要到這個值（= 1%）才承認訊號。
/// - <c>WickToBodyRatio</c>（default=2.0）：Hammer/Shooting Star 的影線/實體比。
/// - <c>EngulfingEnabled</c>（default=1；0 = 禁用）：是否考慮吞噬形態。
/// - <c>Confidence</c>（default=0.65）：訊號 confidence（風控層級用）。
/// </summary>
public sealed class PriceActionPredictorStrategy : IStrategy
{
    public string StrategyType => "PriceAction";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        var lookback = Math.Max(1, (int)config.GetParameter("LookbackPeriod", 20));
        var momentumThreshold = Math.Abs(config.GetParameter("MomentumThreshold", 0.01m));
        var wickRatio = Math.Max(1.0m, config.GetParameter("WickToBodyRatio", 2.0m));
        var engulfingEnabled = config.GetParameter("EngulfingEnabled", 1m) > 0m;
        var confidence = Math.Clamp(config.GetParameter("Confidence", 0.65m), 0.1m, 1.0m);

        var currentPrice = snapshot.FuturesMarkPrice;

        // 需要至少 lookback + 1 根（動能窗口 + 前一根比對 engulfing）
        if (klines.Count < lookback + 1)
            return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));

        var last = klines[^1];
        var prev = klines[^2];

        // 1) 形態偵測
        var patterns = DetectPatterns(last, prev, wickRatio, engulfingEnabled);

        // 2) 動能得分（> 0 偏多、< 0 偏空）
        var momentum = MomentumScore(klines, lookback);

        var existing = openPositions.FirstOrDefault(p => !p.IsClosed);

        // 3) 持倉中：主動反轉平倉（T4 出場智慧）
        if (existing is not null)
        {
            if (existing.Side == PositionSide.Long && patterns.BearishReversal)
                return Task.FromResult(TradingSignal.CloseLong(
                    config.Symbol, currentPrice,
                    $"PA 反轉出場：{patterns.BearishReason}"));

            if (existing.Side == PositionSide.Short && patterns.BullishReversal)
                return Task.FromResult(TradingSignal.CloseShort(
                    config.Symbol, currentPrice,
                    $"PA 反轉出場：{patterns.BullishReason}"));

            return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
        }

        // 4) 無持倉：形態 + 動能雙驗證才進場，避免盤整假訊號
        if (patterns.BullishReversal && momentum >= momentumThreshold)
        {
            var sl = Price.Create(currentPrice.Value * (1 - config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 + config.TakeProfitPercent));
            return Task.FromResult(TradingSignal.OpenLong(
                config.Symbol, currentPrice, sl, tp,
                confidence: confidence,
                reason: $"PA 看多：{patterns.BullishReason} | 動能={momentum:P2}"));
        }

        if (patterns.BearishReversal && momentum <= -momentumThreshold)
        {
            var sl = Price.Create(currentPrice.Value * (1 + config.StopLossPercent));
            var tp = Price.Create(currentPrice.Value * (1 - config.TakeProfitPercent));
            return Task.FromResult(TradingSignal.OpenShort(
                config.Symbol, currentPrice, sl, tp,
                confidence: confidence,
                reason: $"PA 看空：{patterns.BearishReason} | 動能={momentum:P2}"));
        }

        return Task.FromResult(TradingSignal.None(config.Symbol, currentPrice));
    }

    /// <summary>
    /// 動能得分：窗口內每根 K 的 <c>(Close - Open) / Close</c> 加總 → 規範化到 [-1, 1] 量級附近。
    /// 正 = 多頭動能，負 = 空頭動能。用加總而非平均為了讓「連續單向推動」得到放大，
    /// 避免均值化後把趨勢洗回 0。
    /// </summary>
    private static decimal MomentumScore(IReadOnlyList<Kline> klines, int lookback)
    {
        var take = Math.Min(lookback, klines.Count);
        decimal sum = 0m;
        for (int i = klines.Count - take; i < klines.Count; i++)
        {
            var k = klines[i];
            if (k.Close == 0) continue;
            sum += (k.Close - k.Open) / k.Close;
        }
        return sum;
    }

    /// <summary>
    /// 偵測 Hammer / Shooting Star / Bullish Engulfing / Bearish Engulfing。
    /// 回傳兩個語意 flag：多頭反轉 / 空頭反轉。兩者不互斥 — 極端情況同一根可同時具備，
    /// 但現實中動能得分會把一邊淘汰（見 <see cref="AnalyzeAsync"/>）。
    /// </summary>
    private static Patterns DetectPatterns(
        Kline last, Kline prev, decimal wickRatio, bool engulfingEnabled)
    {
        bool bullEngulf = false, bearEngulf = false;
        if (engulfingEnabled)
        {
            // Bullish Engulfing：前陰、當前陽、當前 body 完全覆蓋前一根 body
            bullEngulf = prev.Close < prev.Open
                         && last.Close > last.Open
                         && last.Open <= prev.Close
                         && last.Close >= prev.Open;

            // Bearish Engulfing：前陽、當前陰、當前 body 完全覆蓋前一根 body
            bearEngulf = prev.Close > prev.Open
                         && last.Close < last.Open
                         && last.Open >= prev.Close
                         && last.Close <= prev.Open;
        }

        // Hammer：實體小 + 下影線長 + 上影線短 + 收陽（或至少不收深陰）
        // 避免 BodySize=0（doji）導致 divison by zero：用乘法做比較。
        bool hammer =
            last.BodySize > 0m
            && last.LowerShadow >= last.BodySize * wickRatio
            && last.UpperShadow * 2m <= last.BodySize
            && last.Close >= last.Open;

        // Shooting Star：實體小 + 上影線長 + 下影線短 + 收陰（或至少不收強陽）
        bool shootingStar =
            last.BodySize > 0m
            && last.UpperShadow >= last.BodySize * wickRatio
            && last.LowerShadow * 2m <= last.BodySize
            && last.Close <= last.Open;

        string bullReason = bullEngulf ? "Bullish Engulfing" : hammer ? "Hammer" : string.Empty;
        string bearReason = bearEngulf ? "Bearish Engulfing" : shootingStar ? "Shooting Star" : string.Empty;

        return new Patterns(
            BullishReversal: bullEngulf || hammer,
            BearishReversal: bearEngulf || shootingStar,
            BullishReason: bullReason,
            BearishReason: bearReason);
    }

    private readonly record struct Patterns(
        bool BullishReversal,
        bool BearishReversal,
        string BullishReason,
        string BearishReason);
}
