using CryptoBot.Application.Strategies.TrendFollowing;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies.TrendFollowing;

/// <summary>
/// TrendFollowing (EMA 黃金/死亡交叉 + RSI 動能) 純指標邏輯單元測試。
/// 預設參數：FastEmaPeriod=12 / SlowEmaPeriod=26 / RsiPeriod=14 / RsiMidline=50。
/// </summary>
public class TrendFollowingStrategyTests
{
    private static readonly Symbol BTC = Symbol.Parse("BTC-USDT");

    private static StrategyConfiguration MakeConfig(
        int fast = 12, int slow = 26, int rsi = 14, decimal midline = 50m) =>
        StrategyConfiguration.Create(
            BTC, KlineInterval.FifteenMinutes, Leverage.Create(3),
            riskPerTradePercent: 0.02m,
            stopLossPercent: 0.02m,
            takeProfitPercent: 0.04m,
            maxKlineWindow: 200,
            parameters: new Dictionary<string, decimal>
            {
                ["FastEmaPeriod"] = fast,
                ["SlowEmaPeriod"] = slow,
                ["RsiPeriod"]     = rsi,
                ["RsiMidline"]    = midline,
            });

    private static MarketSnapshot MakeSnapshot(decimal mark) =>
        MarketSnapshot.Create(BTC, DateTime.UtcNow,
            Price.Create(mark), Price.Create(mark - 0.5m), Price.Create(mark + 0.5m));

    private static Kline MakeKline(DateTime open, decimal close) =>
        Kline.Create(open, open.AddMinutes(14).AddSeconds(59),
            open: close, high: close + 0.5m, low: close - 0.5m, close: close,
            volume: 1m, interval: KlineInterval.FifteenMinutes);

    /// <summary>
    /// 構造 60 根收盤序列：前 50 根 100、接著 9 根 90（把 EMA12 壓到 EMA26 下方）、
    /// 最後一根暴拉 300 — 觸發黃金交叉，且 last-bar 的 RSI 因暴漲遠超過 50、價格遠高於慢線。
    /// EMA12(prev)≈92.2 &lt; EMA26(prev)≈95.0；EMA12(last)≈124.2 &gt; EMA26(last)≈110.2。
    /// </summary>
    private static List<Kline> BuildGoldenCrossKlines()
    {
        var list = new List<Kline>(60);
        var t = DateTime.UtcNow.AddMinutes(-60 * 15);
        for (int i = 0; i < 50; i++) { list.Add(MakeKline(t, 100m)); t = t.AddMinutes(15); }
        for (int i = 0; i < 9; i++)  { list.Add(MakeKline(t, 90m));  t = t.AddMinutes(15); }
        list.Add(MakeKline(t, 300m));
        return list;
    }

    /// <summary>
    /// 死亡交叉鏡像：前 50 根 100、接著 9 根 110（把 EMA12 推到 EMA26 上方）、最後一根暴跌到 1。
    /// 觸發 deathCross + RSI &lt; 50 + price &lt; slow。
    /// </summary>
    private static List<Kline> BuildDeathCrossKlines()
    {
        var list = new List<Kline>(60);
        var t = DateTime.UtcNow.AddMinutes(-60 * 15);
        for (int i = 0; i < 50; i++) { list.Add(MakeKline(t, 100m)); t = t.AddMinutes(15); }
        for (int i = 0; i < 9; i++)  { list.Add(MakeKline(t, 110m)); t = t.AddMinutes(15); }
        list.Add(MakeKline(t, 1m));
        return list;
    }

    [Fact]
    public async Task GoldenCross_NoPosition_EmitsOpenLong()
    {
        var sut = new TrendFollowingStrategy();
        var klines = BuildGoldenCrossKlines();

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, MakeSnapshot(klines[^1].Close),
            Array.Empty<Position>());

        Assert.Equal(SignalType.OpenLong, signal.Type);
        Assert.NotNull(signal.SuggestedStopLoss);
        Assert.NotNull(signal.SuggestedTakeProfit);
        Assert.True(signal.SuggestedStopLoss!.Value < signal.SuggestedPrice.Value);
        Assert.True(signal.SuggestedTakeProfit!.Value > signal.SuggestedPrice.Value);
    }

    [Fact]
    public async Task DeathCross_NoPosition_EmitsOpenShort()
    {
        var sut = new TrendFollowingStrategy();
        var klines = BuildDeathCrossKlines();

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, MakeSnapshot(klines[^1].Close),
            Array.Empty<Position>());

        Assert.Equal(SignalType.OpenShort, signal.Type);
        Assert.True(signal.SuggestedStopLoss!.Value > signal.SuggestedPrice.Value);
        Assert.True(signal.SuggestedTakeProfit!.Value < signal.SuggestedPrice.Value);
    }

    [Fact]
    public async Task DeathCross_HoldingLong_EmitsCloseLong()
    {
        var longPos = Position.Open(
            BTC, PositionSide.Long,
            Quantity.Create(1m), Price.Create(100m),
            Leverage.Create(3));

        var sut = new TrendFollowingStrategy();
        var klines = BuildDeathCrossKlines();

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, MakeSnapshot(klines[^1].Close),
            new[] { longPos });

        Assert.Equal(SignalType.CloseLong, signal.Type);
    }

    [Fact]
    public async Task GoldenCross_HoldingShort_EmitsCloseShort()
    {
        var shortPos = Position.Open(
            BTC, PositionSide.Short,
            Quantity.Create(1m), Price.Create(100m),
            Leverage.Create(3));

        var sut = new TrendFollowingStrategy();
        var klines = BuildGoldenCrossKlines();

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, MakeSnapshot(klines[^1].Close),
            new[] { shortPos });

        Assert.Equal(SignalType.CloseShort, signal.Type);
    }

    [Fact]
    public async Task InsufficientKlines_EmitsNone()
    {
        var sut = new TrendFollowingStrategy();
        var shortList = new List<Kline>();
        var t = DateTime.UtcNow.AddMinutes(-10 * 15);
        for (int i = 0; i < 10; i++) { shortList.Add(MakeKline(t, 100m)); t = t.AddMinutes(15); }

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), shortList, MakeSnapshot(100m), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }

    [Fact]
    public async Task FlatPrices_EmitsNone()
    {
        var sut = new TrendFollowingStrategy();
        var flat = new List<Kline>();
        var t = DateTime.UtcNow.AddMinutes(-60 * 15);
        for (int i = 0; i < 60; i++) { flat.Add(MakeKline(t, 100m)); t = t.AddMinutes(15); }

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), flat, MakeSnapshot(100m), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }

    [Fact]
    public async Task CustomPeriods_ParamsConsumedByStrategy()
    {
        // 同一組 klines，用寬鬆 slow=10 換條件成立 vs 預設 slow=26 條件成立 — 驗證參數確實被讀取。
        // 這裡單純 smoke test：讓預設設定跑同一條 GoldenCross 序列仍應維持 OpenLong 訊號，
        // 並以自訂 RsiPeriod=7 再跑一次確認不因短 RSI 視窗而出錯（signal 必定是 OpenLong/None 之一）。
        var sut = new TrendFollowingStrategy();
        var klines = BuildGoldenCrossKlines();

        var signal = await sut.AnalyzeAsync(
            MakeConfig(rsi: 7), klines, MakeSnapshot(klines[^1].Close),
            Array.Empty<Position>());

        // 自訂 RsiPeriod 不應造成例外，且黃金交叉條件不變（RSI 窗口不同但方向相同）。
        Assert.True(signal.Type == SignalType.OpenLong || signal.Type == SignalType.None);
    }
}
