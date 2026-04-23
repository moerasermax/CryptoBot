using CryptoBot.Application.Backtesting;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Backtesting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Backtesting;

/// <summary>
/// S22 T2b — <see cref="BacktestSimulator"/> 滑價公式精準度。
///
/// 公式（Infrastructure 層權威實作）：
///   fill = close × (1 ± SlippageBps / 10_000)
///     Buy  ⇒ +（吃 ask）
///     Sell ⇒ -（吃 bid）
///
/// 這裡用 decimal 精確等值 + 單調性雙線驗證：
/// - Buy 成交價 > Close、Sell 成交價 < Close（方向性）。
/// - 誤差正好 = Close × Bps/10000（數值精度）。
/// - 不同 Bps 輸入（0 / 5 / 50）輸出都依公式線性變動（無隱藏常數）。
///
/// 這組測試同時充當架構契約看門員：只要有人把滑價搬進 Engine，
/// BacktestEngine 的結果就會 × 2，對應期望值立刻失配，S22 類別紅燈。
/// </summary>
[Trait("Category", "S22")]
public class BacktestSimulatorSlippageS22Tests
{
    private static readonly Symbol Btc = Symbol.Create("BTC", "USDT");

    private static Kline BuildKline(decimal close)
    {
        var open = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hi = Math.Max(close, 50_000m);
        var lo = Math.Min(close, 50_000m);
        return Kline.Create(
            openTime: open, closeTime: open.AddMinutes(1),
            open: 50_000m, high: hi, low: lo, close: close,
            volume: 1m, interval: KlineInterval.OneMinute);
    }

    private static BacktestSimulator BuildSimulator(decimal bps)
    {
        var opts = new BacktestOptions
        {
            Symbol = "BTC-USDT",
            Interval = KlineInterval.OneMinute,
            StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            SlippageBps = bps,
            CommissionRate = 0m, // 專注在滑價，不讓手續費噪音干擾
        };
        return new BacktestSimulator(opts, NullLogger<BacktestSimulator>.Instance);
    }

    [Fact]
    public async Task PlaceOrder_Buy_FillPriceIsCloseAdjustedUpByExactBps()
    {
        const decimal close = 50_000m;
        const decimal bps = 5m;                     // 5 bp = 0.05%
        const decimal expected = 50_025m;           // 50_000 × (1 + 0.0005)

        var sim = BuildSimulator(bps);
        sim.AdvanceTo(BuildKline(close));

        var order = Order.CreateMarketOrder(
            Btc, OrderSide.Buy, PositionSide.Long, Quantity.Create(1m));

        await sim.PlaceOrderAsync(order);

        Assert.NotNull(order.AverageFillPrice);
        Assert.Equal(expected, order.AverageFillPrice!.Value);
        Assert.True(order.AverageFillPrice.Value > close, "Buy fill must exceed close.");
    }

    [Fact]
    public async Task PlaceOrder_Sell_FillPriceIsCloseAdjustedDownByExactBps()
    {
        const decimal close = 50_000m;
        const decimal bps = 5m;
        const decimal expected = 49_975m;           // 50_000 × (1 − 0.0005)

        var sim = BuildSimulator(bps);
        sim.AdvanceTo(BuildKline(close));

        var order = Order.CreateMarketOrder(
            Btc, OrderSide.Sell, PositionSide.Long, Quantity.Create(1m));

        await sim.PlaceOrderAsync(order);

        Assert.NotNull(order.AverageFillPrice);
        Assert.Equal(expected, order.AverageFillPrice!.Value);
        Assert.True(order.AverageFillPrice.Value < close, "Sell fill must fall below close.");
    }

    [Fact]
    public async Task PlaceOrder_ZeroBps_FillPriceEqualsClose()
    {
        const decimal close = 123.4567m;

        var sim = BuildSimulator(0m);
        sim.AdvanceTo(BuildKline(close));

        var buy = Order.CreateMarketOrder(Btc, OrderSide.Buy, PositionSide.Long, Quantity.Create(1m));
        var sell = Order.CreateMarketOrder(Btc, OrderSide.Sell, PositionSide.Long, Quantity.Create(1m));

        await sim.PlaceOrderAsync(buy);
        await sim.PlaceOrderAsync(sell);

        Assert.Equal(close, buy.AverageFillPrice!.Value);
        Assert.Equal(close, sell.AverageFillPrice!.Value);
    }

    [Theory]
    [InlineData(5, 25)]    // 50000 × 0.0005 = 25
    [InlineData(50, 250)]  // 50000 × 0.005  = 250
    [InlineData(100, 500)] // 50000 × 0.01   = 500
    public async Task PlaceOrder_SlippageIsLinearInBps(int bps, int expectedDelta)
    {
        const decimal close = 50_000m;
        var bpsDec = (decimal)bps;
        var deltaDec = (decimal)expectedDelta;

        var sim = BuildSimulator(bpsDec);
        sim.AdvanceTo(BuildKline(close));

        var buy = Order.CreateMarketOrder(Btc, OrderSide.Buy, PositionSide.Long, Quantity.Create(1m));
        var sell = Order.CreateMarketOrder(Btc, OrderSide.Sell, PositionSide.Long, Quantity.Create(1m));

        await sim.PlaceOrderAsync(buy);
        await sim.PlaceOrderAsync(sell);

        Assert.Equal(close + deltaDec, buy.AverageFillPrice!.Value);
        Assert.Equal(close - deltaDec, sell.AverageFillPrice!.Value);
    }

    [Fact]
    public async Task PlaceOrder_BuyAndSell_FillPricesAreSymmetricAroundClose()
    {
        const decimal close = 42_123.45m;
        const decimal bps = 7m;

        var sim = BuildSimulator(bps);
        sim.AdvanceTo(BuildKline(close));

        var buy = Order.CreateMarketOrder(Btc, OrderSide.Buy, PositionSide.Long, Quantity.Create(1m));
        var sell = Order.CreateMarketOrder(Btc, OrderSide.Sell, PositionSide.Long, Quantity.Create(1m));

        await sim.PlaceOrderAsync(buy);
        await sim.PlaceOrderAsync(sell);

        var buyDelta = buy.AverageFillPrice!.Value - close;
        var sellDelta = close - sell.AverageFillPrice!.Value;

        Assert.Equal(buyDelta, sellDelta);
        Assert.Equal(close * (bps / 10_000m), buyDelta);
    }
}
