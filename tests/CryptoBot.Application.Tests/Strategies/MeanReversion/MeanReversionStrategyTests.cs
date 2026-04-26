using CryptoBot.Application.Strategies.MeanReversion;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies.MeanReversion;

/// <summary>
/// MeanReversion (BB 觸軌 + RSI 極端) 純指標邏輯單元測試。
/// S24：param key 與 B46RsiBb 對齊（BbPeriod / BbStdDev）— 此處同樣的 key 同樣的語意。
/// 預設參數：BbPeriod=20 / BbStdDev=2 / RsiPeriod=14 / RsiOversold=30 / RsiOverbought=70。
/// </summary>
public class MeanReversionStrategyTests
{
    private static readonly Symbol Sym = Symbol.Parse("BTC-USDT");

    private static StrategyConfiguration MakeConfig(Dictionary<string, decimal>? overrides = null)
    {
        var p = new Dictionary<string, decimal>
        {
            ["BbPeriod"]      = 20m,
            ["BbStdDev"]      = 2m,
            ["RsiPeriod"]     = 14m,
            ["RsiOversold"]   = 30m,
            ["RsiOverbought"] = 70m,
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
    /// 25 根小震盪穩定 BB 中軌，再 10 根連續下跌（-2/bar）— 最後一根收盤遠低於 BB 下軌，RSI 跌破 30。
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

    /// <summary>
    /// 25 根小震盪 + 10 根連續上漲（+2/bar）— 最後一根收盤高於 BB 上軌，RSI 站上 70。
    /// </summary>
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
        var sut = new MeanReversionStrategy();
        var klines = new List<Kline> { MakeKline(100m, 0), MakeKline(101m, 15) };

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, Snapshot(101m), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }

    [Fact]
    public async Task CrashBelowLowerBand_WithRsiOversold_EmitsOpenLong()
    {
        var sut = new MeanReversionStrategy();
        var klines = CrashSeries();

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        Assert.Equal(SignalType.OpenLong, signal.Type);
        Assert.NotNull(signal.SuggestedStopLoss);
        Assert.NotNull(signal.SuggestedTakeProfit);
        Assert.True(signal.SuggestedStopLoss!.Value < signal.SuggestedPrice.Value);
        Assert.True(signal.SuggestedTakeProfit!.Value > signal.SuggestedPrice.Value);
    }

    [Fact]
    public async Task SurgeAboveUpperBand_WithRsiOverbought_EmitsOpenShort()
    {
        var sut = new MeanReversionStrategy();
        var klines = SurgeSeries();

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        Assert.Equal(SignalType.OpenShort, signal.Type);
        Assert.True(signal.SuggestedStopLoss!.Value > signal.SuggestedPrice.Value);
        Assert.True(signal.SuggestedTakeProfit!.Value < signal.SuggestedPrice.Value);
    }

    [Fact]
    public async Task ExistingLong_PriceRevertsToMiddle_EmitsCloseLong()
    {
        var sut = new MeanReversionStrategy();
        // 先建 crash 序列（讓 BB 中軌在 98~100 區間），再把最後一根拉回 100 — 觸發平多。
        var klines = CrashSeries();
        klines[^1] = MakeKline(100m, 34 * 15);

        var longPos = Position.Open(
            Sym, PositionSide.Long, Quantity.Create(1m),
            Price.Create(90m), Leverage.Moderate);

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, Snapshot(klines[^1].Close), new[] { longPos });

        Assert.Equal(SignalType.CloseLong, signal.Type);
    }

    [Fact]
    public async Task ExistingShort_PriceRevertsToMiddle_EmitsCloseShort()
    {
        var sut = new MeanReversionStrategy();
        // 先建 surge 序列（BB 中軌往上走），再把最後一根拉回 100 — 觸發平空。
        var klines = SurgeSeries();
        klines[^1] = MakeKline(100m, 34 * 15);

        var shortPos = Position.Open(
            Sym, PositionSide.Short, Quantity.Create(1m),
            Price.Create(110m), Leverage.Moderate);

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, Snapshot(klines[^1].Close), new[] { shortPos });

        Assert.Equal(SignalType.CloseShort, signal.Type);
    }

    [Fact]
    public async Task NeutralPrice_ReturnsNone()
    {
        var sut = new MeanReversionStrategy();
        // 30 根平穩微震盪 — RSI 會接近 50，BB 通道狹窄但 close 不會同時滿足越界+RSI 極端。
        var klines = new List<Kline>();
        for (var i = 0; i < 30; i++)
            klines.Add(MakeKline(100m + (i % 2 == 0 ? 0.1m : -0.1m), i * 15));

        var signal = await sut.AnalyzeAsync(
            MakeConfig(), klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }

    [Fact]
    public async Task CustomParams_StrictOversoldSuppressesLong()
    {
        // Oversold=5 極端嚴格 — CrashSeries 的 RSI 跌不到 5，驗證策略確實消費 RsiOversold 參數。
        var sut = new MeanReversionStrategy();
        var cfg = MakeConfig(new Dictionary<string, decimal> { ["RsiOversold"] = 5m });
        var klines = CrashSeries();

        var signal = await sut.AnalyzeAsync(
            cfg, klines, Snapshot(klines[^1].Close), Array.Empty<Position>());

        Assert.Equal(SignalType.None, signal.Type);
    }
}
