using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Synchronization;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.Synchronization;

/// <summary>
/// S66-E：包夾測量演算法的數學正確性。
/// </summary>
public class SkewMeasurementServiceTests
{
    [Fact]
    public async Task MidPointCalculation_LocalMid_IsAverageOfBeforeAndAfter()
    {
        // Arrange — 用 FakeTimeProvider 鎖定 localBefore 與 localAfter 時間戳
        var t0 = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(500);  // localAfter = before + 500ms（round-trip 500ms）
        var clock = new SteppingClock(t0, t1);

        // Server 固定回 t0 + 250ms（中點 = 真實 server 時間 → offset 應為 0）
        var exchange = new ServerTimeStubExchange(serverTime: t0.AddMilliseconds(250));
        var sut = new SkewMeasurementService(exchange, clock);

        // Act
        var m = await sut.MeasureAsync();

        // Assert
        Assert.Equal(t0, m.LocalBeforeUtc);
        Assert.Equal(t1, m.LocalAfterUtc);
        Assert.Equal(t0.AddMilliseconds(250), m.LocalMidUtc);  // mid = average
        Assert.Equal(TimeSpan.Zero, m.Offset);                 // server 完全對齊 mid
        Assert.Equal(TimeSpan.FromMilliseconds(500), m.RoundTrip);
    }

    [Fact]
    public async Task ServerAheadByOneSecond_OffsetIsPositive()
    {
        var t0 = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(100);
        var clock = new SteppingClock(t0, t1);

        // 伺服器領先 1000ms（local mid = t0+50ms，server = t0+1050ms → offset = +1000ms）
        var exchange = new ServerTimeStubExchange(serverTime: t0.AddMilliseconds(1050));
        var sut = new SkewMeasurementService(exchange, clock);

        var m = await sut.MeasureAsync();

        Assert.Equal(1000, (long)m.Offset.TotalMilliseconds);
    }

    [Fact]
    public async Task ServerBehindByOneSecond_OffsetIsNegative()
    {
        var t0 = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(100);
        var clock = new SteppingClock(t0, t1);

        // 伺服器落後 1000ms（local mid = t0+50ms，server = t0-950ms → offset = -1000ms）
        var exchange = new ServerTimeStubExchange(serverTime: t0.AddMilliseconds(-950));
        var sut = new SkewMeasurementService(exchange, clock);

        var m = await sut.MeasureAsync();

        Assert.Equal(-1000, (long)m.Offset.TotalMilliseconds);
    }

    [Fact]
    public async Task ExchangeThrows_PropagatesException()
    {
        var clock = TimeProvider.System;
        var exchange = new ServerTimeStubExchange(throwOnGet: new InvalidOperationException("boom"));
        var sut = new SkewMeasurementService(exchange, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.MeasureAsync());
    }
}

// ============== Test fakes ==============

/// <summary>每次 GetUtcNow 回連續設定的時間（first call → t0, second call → t1, ...）</summary>
internal sealed class SteppingClock : TimeProvider
{
    private readonly Queue<DateTime> _times;

    public SteppingClock(params DateTime[] times)
    {
        _times = new Queue<DateTime>(times);
    }

    public override DateTimeOffset GetUtcNow()
    {
        if (_times.Count == 0) throw new InvalidOperationException("SteppingClock exhausted");
        var t = _times.Dequeue();
        return new DateTimeOffset(t, TimeSpan.Zero);
    }
}

internal sealed class ServerTimeStubExchange : IExchangeClient
{
    private readonly DateTime _serverTime;
    private readonly Exception? _throwOnGet;

    public ServerTimeStubExchange(DateTime serverTime = default, Exception? throwOnGet = null)
    {
        _serverTime = serverTime == default ? DateTime.UtcNow : serverTime;
        _throwOnGet = throwOnGet;
    }

    public Task<DateTime> GetServerTimeAsync(CancellationToken ct = default)
    {
        if (_throwOnGet is not null) throw _throwOnGet;
        return Task.FromResult(_serverTime);
    }

    // ===== 其餘介面方法 =====
    public string ExchangeName => "Stub";
    public string QuoteAsset => "VST";
    public TradingMode CurrentMode => TradingMode.Demo;
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;
    public Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default) => Task.FromResult(0m);
    public Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Kline>> GetKlinesAsync(Symbol symbol, KlineInterval interval, int limit = 500, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Kline>>(Array.Empty<Kline>());
    public Task<Price> GetMarkPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(Price.Create(50000m));
    public Task<Price> GetSpotPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(Price.Create(50000m));
    public Task<MarketSnapshot> GetMarketSnapshotAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(MarketSnapshot.Create(symbol, DateTime.UtcNow,
            Price.Create(50000m), Price.Create(50000m), Price.Create(50000m)));
    public Task<SymbolTradingRules> GetTradingRulesAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(new SymbolTradingRules(symbol, 0.0001m, decimal.MaxValue, 0.0001m, 0.1m, 5m, 125));
    public Task PlaceOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ExchangeOrderSnapshot?> GetOrderByClientOrderIdAsync(Symbol symbol, string clientOrderId, CancellationToken ct = default) =>
        Task.FromResult<ExchangeOrderSnapshot?>(null);
    public Task<IReadOnlyList<ExchangeOpenOrderInfo>> GetOpenOrdersAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeOpenOrderInfo>>(Array.Empty<ExchangeOpenOrderInfo>());
    public Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangePositionInfo>>(Array.Empty<ExchangePositionInfo>());
}
