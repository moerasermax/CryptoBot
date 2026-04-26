using CryptoBot.Application.Strategies.B46RsiBb;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies;

/// <summary>
/// B46 RSI + Bollinger 策略單元測試。
/// 進場/出場/冷啟動/自訂參數讀取分別各一個 fact；預設參數 RSI(14) / BB(20, 2σ)。
/// </summary>
public class B46RsiBbStrategyTests
{
    private static readonly Symbol Sym = Symbol.Parse("BTC-USDT");

    private static StrategyConfiguration MakeConfig(Dictionary<string, decimal>? overrides = null)
    {
        var p = new Dictionary<string, decimal>
        {
            ["RsiPeriod"]     = 14m,
            ["RsiOversold"]   = 30m,
            ["RsiOverbought"] = 70m,
            ["BbPeriod"]      = 20m,
            ["BbStdDev"]      = 2m,
        };
        if (overrides is not null)
            foreach (var kv in overrides) p[kv.Key] = kv.Value;

        return StrategyConfiguration.Create(
            symbol: Sym,
            interval: KlineInterval.FifteenMinutes,
            leverage: Leverage.Moderate,
            stopLossPercent: 0.02m,
            takeProfitPercent: 0.04m,
            parameters: p);
    }

    private static MarketSnapshot Snapshot(decimal mark) =>
        MarketSnapshot.Create(
            Sym, DateTime.UtcNow,
            Price.Create(mark), Price.Create(mark - 0.5m), Price.Create(mark + 0.5m));

    private static Kline MakeKline(decimal close, int offsetMinutes)
    {
        var open = close;
        var high = Math.Max(open, close) + 0.01m;
        var low  = Math.Min(open, close) - 0.01m;
        var t    = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(offsetMinutes);
        return Kline.Create(t, t.AddMinutes(15), open, high, low, close, 1m, KlineInterval.FifteenMinutes);
    }

    /// <summary>
    /// 先 25 根小幅震盪穩定 BB 中軌，再 10 根連續下跌 — 最後一根收盤遠低於 BB 下軌，RSI 跌破 30。
    /// </summary>
    private static List<Kline> CrashSeries()
    {
        var list = new List<Kline>();
        for (var i = 0; i < 25; i++)
            list.Add(MakeKline(100m + (i % 2 == 0 ? 0.2m : -0.2m), i * 15));
        for (var i = 0; i < 10; i++)
            list.Add(MakeKline(100m - (i + 1) * 2m, (25 + i) * 15));
        return list;
    }

    private static List<Kline> SurgeSeries()
    {
        var list = new List<Kline>();
        for (var i = 0; i < 25; i++)
            list.Add(MakeKline(100m + (i % 2 == 0 ? 0.2m : -0.2m), i * 15));
        for (var i = 0; i < 10; i++)
            list.Add(MakeKline(100m + (i + 1) * 2m, (25 + i) * 15));
        return list;
    }

    [Fact]
    public async Task InsufficientKlines_ReturnsNone()
    {
        var strat = new B46RsiBbStrategy();
        var cfg = MakeConfig();
        var klines = new List<Kline> { MakeKline(100m, 0), MakeKline(101m, 15) };

        var signal = await strat.AnalyzeAsync(cfg, klines, Snapshot(101m), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }

    [Fact]
    public async Task CrashBelowLowerBandAndRsiOversold_OpensLong()
    {
        var strat = new B46RsiBbStrategy();
        var cfg = MakeConfig();
        var klines = CrashSeries();

        var signal = await strat.AnalyzeAsync(cfg, klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        Assert.Equal(SignalType.OpenLong, signal.Type);
        Assert.NotNull(signal.SuggestedStopLoss);
        Assert.NotNull(signal.SuggestedTakeProfit);
        Assert.True(signal.SuggestedStopLoss!.Value < signal.SuggestedPrice.Value);
        Assert.True(signal.SuggestedTakeProfit!.Value > signal.SuggestedPrice.Value);
    }

    [Fact]
    public async Task SurgeAboveUpperBandAndRsiOverbought_OpensShort()
    {
        var strat = new B46RsiBbStrategy();
        var cfg = MakeConfig();
        var klines = SurgeSeries();

        var signal = await strat.AnalyzeAsync(cfg, klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        Assert.Equal(SignalType.OpenShort, signal.Type);
        Assert.NotNull(signal.SuggestedStopLoss);
        Assert.NotNull(signal.SuggestedTakeProfit);
        Assert.True(signal.SuggestedStopLoss!.Value > signal.SuggestedPrice.Value);
        Assert.True(signal.SuggestedTakeProfit!.Value < signal.SuggestedPrice.Value);
    }

    [Fact]
    public async Task ExistingLong_PriceBackToMiddle_ClosesLong()
    {
        var strat = new B46RsiBbStrategy();
        var cfg = MakeConfig();
        // 造一段先跌後反彈的序列：25 根穩定 + 9 根下跌 + 最後一根拉回到 BB 中軌上方
        var klines = CrashSeries();
        klines[^1] = MakeKline(100m, 34 * 15);

        var openLong = Position.Open(
            Sym, PositionSide.Long, Quantity.Create(1m),
            Price.Create(90m), Leverage.Moderate);

        var signal = await strat.AnalyzeAsync(
            cfg, klines, Snapshot(klines[^1].Close), new[] { openLong });

        Assert.Equal(SignalType.CloseLong, signal.Type);
    }

    [Fact]
    public async Task NeutralPrice_ReturnsNone()
    {
        var strat = new B46RsiBbStrategy();
        var cfg = MakeConfig();
        // 平穩微震盪 30 根 — RSI 會在 50 附近，BB 通道狹窄但 close 不會同時滿足越界 + RSI 極端。
        var klines = new List<Kline>();
        for (var i = 0; i < 30; i++)
            klines.Add(MakeKline(100m + (i % 2 == 0 ? 0.1m : -0.1m), i * 15));

        var signal = await strat.AnalyzeAsync(cfg, klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }

    [Fact]
    public async Task CustomParams_OversoldThresholdRespected()
    {
        // 非常嚴格的 Oversold=5 — 驗證策略確實讀了參數而不是用寫死的 30
        var strat = new B46RsiBbStrategy();
        var cfg = MakeConfig(new Dictionary<string, decimal> { ["RsiOversold"] = 5m });
        var klines = CrashSeries();

        var signal = await strat.AnalyzeAsync(cfg, klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        // 同樣的 CrashSeries 在 Oversold=30 下會觸發 Long（另一個 fact 已驗證），
        // 但 Oversold=5 下 RSI 不可能跌這麼低，所以必為 None — 參數成功影響決策。
        Assert.Equal(SignalType.None, signal.Type);
    }
}
