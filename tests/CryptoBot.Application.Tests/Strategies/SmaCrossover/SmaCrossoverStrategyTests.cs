using CryptoBot.Application.Strategies;
using CryptoBot.Application.Strategies.SmaCrossover;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies.SmaCrossover;

/// <summary>
/// SMA 20/50 交叉策略的純指標邏輯單元測試。
/// 不過 DI — 直接呼叫 AnalyzeAsync，驗證訊號類型。
/// </summary>
public class SmaCrossoverStrategyTests
{
    private static readonly Symbol BTC = Symbol.Parse("BTC-USDT");

    private static StrategyConfiguration MakeConfig(int fast = 20, int slow = 50) =>
        StrategyConfiguration.Create(
            BTC, KlineInterval.FifteenMinutes, Leverage.Create(3),
            riskPerTradePercent: 0.02m,
            stopLossPercent: 0.02m,
            takeProfitPercent: 0.04m,
            maxKlineWindow: 200,
            parameters: new Dictionary<string, decimal>
            {
                ["FastSmaPeriod"] = fast,
                ["SlowSmaPeriod"] = slow,
            });

    private static MarketSnapshot MakeSnapshot(decimal mark) =>
        MarketSnapshot.Create(BTC, DateTime.UtcNow,
            Price.Create(mark), Price.Create(mark - 0.5m), Price.Create(mark + 0.5m));

    private static Kline MakeKline(DateTime open, decimal close) =>
        Kline.Create(open, open.AddMinutes(14).AddSeconds(59),
            open: close, high: close + 0.5m, low: close - 0.5m, close: close,
            volume: 1m, interval: KlineInterval.FifteenMinutes);

    /// <summary>
    /// 構造 60 根收盤序列，觸發 fast=20 / slow=50 的黃金交叉：
    /// 前 50 根 100 / 接著 9 根 90（把 SMA20 壓到 SMA50 下方）/ 最後 1 根劇烈拉升至 300。
    /// 驗算：
    ///   prev(i=58, close=90) → SMA20 = (11×100 + 9×90)/20 = 95.5, SMA50 = (41×100+9×90)/50 = 98.2
    ///   last(i=59, close=300)→ SMA20 = (10×100 + 9×90 + 300)/20 = 105.5, SMA50 = (40×100+9×90+300)/50 = 102.2
    ///   ⇒ fast_prev ≤ slow_prev 且 fast_last &gt; slow_last → golden cross。
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
    /// 死亡交叉：前 50 根 100 / 接著 9 根 110（把 SMA20 推到 SMA50 上方）/ 最後 1 根暴跌到 1。
    /// 驗算：
    ///   prev(i=58, close=110) → SMA20 = (11×100+9×110)/20 = 104.5, SMA50 = (41×100+9×110)/50 = 101.8
    ///   last(i=59, close=1)   → SMA20 = (10×100+9×110+1)/20 = 99.55, SMA50 = (40×100+9×110+1)/50 = 99.82
    ///   ⇒ fast_prev ≥ slow_prev 且 fast_last &lt; slow_last → death cross。
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
        var sut = new SmaCrossoverStrategy();
        var signal = await sut.AnalyzeAsync(
            MakeConfig(), BuildGoldenCrossKlines(), MakeSnapshot(100m),
            Array.Empty<Position>());

        Assert.Equal(SignalType.OpenLong, signal.Type);
    }

    [Fact]
    public async Task DeathCross_NoPosition_EmitsOpenShort()
    {
        var sut = new SmaCrossoverStrategy();
        var signal = await sut.AnalyzeAsync(
            MakeConfig(), BuildDeathCrossKlines(), MakeSnapshot(100m),
            Array.Empty<Position>());

        Assert.Equal(SignalType.OpenShort, signal.Type);
    }

    [Fact]
    public async Task GoldenCross_HoldingLong_EmitsNone()
    {
        var longPos = Position.Open(
            BTC, PositionSide.Long,
            Quantity.Create(1m), Price.Create(100m),
            Leverage.Create(3));

        var sut = new SmaCrossoverStrategy();
        var signal = await sut.AnalyzeAsync(
            MakeConfig(), BuildGoldenCrossKlines(), MakeSnapshot(100m),
            new[] { longPos });

        Assert.Equal(SignalType.None, signal.Type);
    }

    [Fact]
    public async Task DeathCross_HoldingLong_EmitsCloseLong()
    {
        var longPos = Position.Open(
            BTC, PositionSide.Long,
            Quantity.Create(1m), Price.Create(100m),
            Leverage.Create(3));

        var sut = new SmaCrossoverStrategy();
        var signal = await sut.AnalyzeAsync(
            MakeConfig(), BuildDeathCrossKlines(), MakeSnapshot(100m),
            new[] { longPos });

        Assert.Equal(SignalType.CloseLong, signal.Type);
    }

    [Fact]
    public async Task InsufficientKlines_EmitsNone()
    {
        var sut = new SmaCrossoverStrategy();
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
        var sut = new SmaCrossoverStrategy();
        var flat = new List<Kline>();
        var t = DateTime.UtcNow.AddMinutes(-60 * 15);
        for (int i = 0; i < 60; i++) { flat.Add(MakeKline(t, 100m)); t = t.AddMinutes(15); }

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), flat, MakeSnapshot(100m), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }
}
