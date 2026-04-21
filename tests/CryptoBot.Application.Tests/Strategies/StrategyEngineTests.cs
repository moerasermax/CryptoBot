using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Notifications;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies;

/// <summary>
/// S3 + S4 管線測試 — 用輕量 inline fake 覆蓋 cooldown / sizer / 全管線。
/// </summary>
public class StrategyEngineTests
{
    // ─────────────── 測試用共用 builder ───────────────

    private static StrategyConfiguration MakeConfig(TimeSpan? cooldown = null) =>
        StrategyConfiguration.Create(
            symbol: Symbol.Parse("BTC-USDT"),
            interval: KlineInterval.FifteenMinutes,
            leverage: Leverage.Moderate,
            riskPerTradePercent: 0.02m,
            stopLossPercent: 0.02m,
            takeProfitPercent: 0.04m,
            cooldownPeriod: cooldown);

    private static Strategy MakeStrategy(StrategyConfiguration? cfg = null)
    {
        var s = Strategy.Create("TestStrat", "TestType", cfg ?? MakeConfig());
        s.Start();
        return s;
    }

    private static MarketSnapshot MakeSnapshot(decimal mark = 100m) =>
        MarketSnapshot.Create(
            Symbol.Parse("BTC-USDT"), DateTime.UtcNow,
            Price.Create(mark), Price.Create(mark - 0.5m), Price.Create(mark + 0.5m));

    // ─────────────── StrategyCooldownTracker ───────────────

    [Fact]
    public void CooldownTracker_NoRecord_NotInCooldown()
    {
        var tracker = new StrategyCooldownTracker();
        Assert.False(tracker.IsInCooldown(Guid.NewGuid(), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CooldownTracker_RecordedAndWithinWindow_IsInCooldown()
    {
        var clock = new FakeTimeProvider();
        var tracker = new StrategyCooldownTracker(clock);
        var id = Guid.NewGuid();

        tracker.RecordOrderPlaced(id);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(tracker.IsInCooldown(id, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CooldownTracker_PastWindow_NotInCooldown()
    {
        var clock = new FakeTimeProvider();
        var tracker = new StrategyCooldownTracker(clock);
        var id = Guid.NewGuid();

        tracker.RecordOrderPlaced(id);
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(tracker.IsInCooldown(id, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CooldownTracker_ZeroCooldown_NeverActive()
    {
        var tracker = new StrategyCooldownTracker();
        var id = Guid.NewGuid();
        tracker.RecordOrderPlaced(id);
        Assert.False(tracker.IsInCooldown(id, TimeSpan.Zero));
    }

    // ─────────────── OrderSizer ───────────────

    [Fact]
    public async Task OrderSizer_ComputesQuantityFromRiskFormula()
    {
        // balance 10_000, risk 2%, entry 100, SL 2% => riskAmount=200, stopDist=2 => qty=100
        var exchange = new FakeExchangeClient { Balance = 10_000m };
        var sizer = new OrderSizer(exchange);
        var strategy = MakeStrategy();
        var signal = TradingSignal.OpenLong(
            Symbol.Parse("BTC-USDT"), Price.Create(100m),
            stopLoss: Price.Create(98m), takeProfit: Price.Create(104m),
            confidence: 0.8m, reason: "test");

        var qty = await sizer.ComputeAsync(strategy, signal);

        Assert.Equal(100m, qty.Value);
    }

    [Fact]
    public async Task OrderSizer_ZeroBalance_ReturnsZero()
    {
        var exchange = new FakeExchangeClient { Balance = 0m };
        var sizer = new OrderSizer(exchange);
        var strategy = MakeStrategy();
        var signal = TradingSignal.OpenLong(
            Symbol.Parse("BTC-USDT"), Price.Create(100m),
            Price.Create(98m), Price.Create(104m), 0.8m, "test");

        var qty = await sizer.ComputeAsync(strategy, signal);

        Assert.Equal(0m, qty.Value);
    }

    // ─────────────── RiskManager cooldown integration ───────────────

    [Fact]
    public async Task RiskManager_InCooldown_Rejects()
    {
        var clock = new FakeTimeProvider();
        var tracker = new StrategyCooldownTracker(clock);
        var positions = new FakePositionRepository();
        var exchange = new FakeExchangeClient { Balance = 10_000m };

        var risk = new RiskManager(exchange, positions, tracker, RiskLimits.Moderate);
        var strategy = MakeStrategy(MakeConfig(cooldown: TimeSpan.FromSeconds(30)));
        var signal = TradingSignal.OpenLong(
            Symbol.Parse("BTC-USDT"), Price.Create(100m),
            Price.Create(98m), Price.Create(104m), 0.8m, "t");

        tracker.RecordOrderPlaced(strategy.Id);
        clock.Advance(TimeSpan.FromSeconds(5));

        var result = await risk.CheckBeforeOpenAsync(strategy, signal, Quantity.Create(1m));

        Assert.False(result.IsApproved);
        Assert.Contains("cooldown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RiskManager_AfterCooldownExpires_Approves()
    {
        var clock = new FakeTimeProvider();
        var tracker = new StrategyCooldownTracker(clock);
        var positions = new FakePositionRepository();
        var exchange = new FakeExchangeClient { Balance = 100_000m };

        var risk = new RiskManager(exchange, positions, tracker, RiskLimits.Moderate);
        var strategy = MakeStrategy(MakeConfig(cooldown: TimeSpan.FromSeconds(30)));
        var signal = TradingSignal.OpenLong(
            Symbol.Parse("BTC-USDT"), Price.Create(100m),
            Price.Create(98m), Price.Create(104m), 0.8m, "t");

        tracker.RecordOrderPlaced(strategy.Id);
        clock.Advance(TimeSpan.FromSeconds(31));

        var result = await risk.CheckBeforeOpenAsync(strategy, signal, Quantity.Create(1m));

        Assert.True(result.IsApproved, $"Rejected: {result.Reason}");
    }

    // ─────────────── StrategyExecutor end-to-end ───────────────

    [Fact]
    public async Task StrategyExecutor_SignalOpenLong_PlacesOrderAndRecordsCooldown()
    {
        // Arrange
        var exchange = new FakeExchangeClient { Balance = 100_000m };
        exchange.PreloadKlines = MakeKlines(60);
        var marketData = new FakeMarketDataStream();
        var tracker = new StrategyCooldownTracker();
        var orderRepo = new FakeOrderRepository();
        var positionRepo = new FakePositionRepository();
        var unitOfWork = new FakeUnitOfWork();

        var sp = new FakeServiceProvider()
            .Register<IOrderRepository>(orderRepo)
            .Register<IPositionRepository>(positionRepo)
            .Register<IStrategyRepository>(new FakeStrategyRepository())
            .Register<IUnitOfWork>(unitOfWork)
            .Register<IOrderSizer>(new OrderSizer(exchange))
            .Register<IRiskManager>(new RiskManager(exchange, positionRepo, tracker, RiskLimits.Moderate));

        var strategy = MakeStrategy();
        var strategyImpl = new FakeStrategy(signalFor: SignalType.OpenLong);

        await using var executor = new StrategyExecutor(
            strategy, strategyImpl, marketData, exchange, tracker,
            sp, new NoOpNotificationService(), new NullRealtimeBroadcaster(),
            NullLogger<StrategyExecutor>.Instance);

        await executor.StartAsync();

        // Act — 模擬一根新的收盤 K 線
        var newKline = MakeKline(DateTime.UtcNow, 100m, 101m);
        await marketData.FireKlineUpdateAsync(
            strategy.Configuration.Symbol, strategy.Configuration.Interval, newKline);

        // Assert — 下單 → 寫入 repo → 記錄冷卻
        Assert.Equal(1, exchange.PlaceOrderCalls);
        Assert.Single(orderRepo.Added);
        Assert.Equal(1, unitOfWork.SaveChangesCalls);
        Assert.True(tracker.IsInCooldown(strategy.Id, strategy.Configuration.CooldownPeriod));

        var order = orderRepo.Added[0];
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(PositionSide.Long, order.PositionSide);

        await executor.StopAsync();
    }

    [Fact]
    public async Task StrategyExecutor_SignalNone_DoesNotPlaceOrder()
    {
        var exchange = new FakeExchangeClient { Balance = 100_000m };
        exchange.PreloadKlines = MakeKlines(60);
        var marketData = new FakeMarketDataStream();
        var tracker = new StrategyCooldownTracker();

        var sp = new FakeServiceProvider()
            .Register<IOrderRepository>(new FakeOrderRepository())
            .Register<IPositionRepository>(new FakePositionRepository())
            .Register<IStrategyRepository>(new FakeStrategyRepository())
            .Register<IUnitOfWork>(new FakeUnitOfWork())
            .Register<IOrderSizer>(new OrderSizer(exchange))
            .Register<IRiskManager>(new RiskManager(exchange, new FakePositionRepository(), tracker, RiskLimits.Moderate));

        var strategy = MakeStrategy();
        var impl = new FakeStrategy(signalFor: SignalType.None);

        await using var executor = new StrategyExecutor(
            strategy, impl, marketData, exchange, tracker, sp,
            new NoOpNotificationService(), new NullRealtimeBroadcaster(),
            NullLogger<StrategyExecutor>.Instance);
        await executor.StartAsync();

        await marketData.FireKlineUpdateAsync(
            strategy.Configuration.Symbol, strategy.Configuration.Interval, MakeKline(DateTime.UtcNow, 100m, 101m));

        Assert.Equal(0, exchange.PlaceOrderCalls);
    }

    [Fact]
    public async Task StrategyExecutor_CooldownBlocksSecondOrder()
    {
        var exchange = new FakeExchangeClient { Balance = 100_000m };
        exchange.PreloadKlines = MakeKlines(60);
        var marketData = new FakeMarketDataStream();
        var tracker = new StrategyCooldownTracker();
        var orderRepo = new FakeOrderRepository();
        var positionRepo = new FakePositionRepository();

        var sp = new FakeServiceProvider()
            .Register<IOrderRepository>(orderRepo)
            .Register<IPositionRepository>(positionRepo)
            .Register<IStrategyRepository>(new FakeStrategyRepository())
            .Register<IUnitOfWork>(new FakeUnitOfWork())
            .Register<IOrderSizer>(new OrderSizer(exchange))
            .Register<IRiskManager>(new RiskManager(exchange, positionRepo, tracker, RiskLimits.Moderate));

        var strategy = MakeStrategy(MakeConfig(cooldown: TimeSpan.FromSeconds(30)));
        var impl = new FakeStrategy(signalFor: SignalType.OpenLong);

        await using var executor = new StrategyExecutor(
            strategy, impl, marketData, exchange, tracker, sp,
            new NoOpNotificationService(), new NullRealtimeBroadcaster(),
            NullLogger<StrategyExecutor>.Instance);
        await executor.StartAsync();

        // 第一根 → 下單成功
        await marketData.FireKlineUpdateAsync(
            strategy.Configuration.Symbol, strategy.Configuration.Interval, MakeKline(DateTime.UtcNow, 100m, 101m));
        Assert.Equal(1, exchange.PlaceOrderCalls);

        // 第二根（立刻進來）→ 被冷卻擋下
        await marketData.FireKlineUpdateAsync(
            strategy.Configuration.Symbol, strategy.Configuration.Interval, MakeKline(DateTime.UtcNow.AddSeconds(1), 101m, 102m));
        Assert.Equal(1, exchange.PlaceOrderCalls);  // 仍是 1
    }

    // ─────────────── Helpers ───────────────

    private static IReadOnlyList<Kline> MakeKlines(int n)
    {
        var list = new List<Kline>(n);
        var t = DateTime.UtcNow.AddMinutes(-n * 15);
        for (int i = 0; i < n; i++)
        {
            list.Add(MakeKline(t, 100m + i * 0.01m, 100m + i * 0.02m));
            t = t.AddMinutes(15);
        }
        return list;
    }

    private static Kline MakeKline(DateTime open, decimal o, decimal c) =>
        Kline.Create(
            open, open.AddMinutes(14).AddSeconds(59),
            o, Math.Max(o, c) + 0.5m, Math.Min(o, c) - 0.5m, c,
            volume: 10m, interval: KlineInterval.FifteenMinutes);
}

// ═════════════════════════════════════════════════════════
// Inline fakes
// ═════════════════════════════════════════════════════════

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 4, 20, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan span) => _now = _now.Add(span);
}

internal sealed class FakeServiceProvider : IServiceProvider, IServiceScope, IServiceScopeFactory
{
    private readonly Dictionary<Type, object> _map = new();

    public FakeServiceProvider Register<T>(T instance) where T : class
    {
        _map[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceScopeFactory)) return this;
        return _map.TryGetValue(serviceType, out var v) ? v : null;
    }

    public IServiceProvider ServiceProvider => this;
    public IServiceScope CreateScope() => this;
    public void Dispose() { }
}

internal sealed class FakeMarketDataStream : IMarketDataStream
{
    public event Func<Symbol, KlineInterval, Kline, Task>? OnKlineUpdate;
    public event Func<Symbol, Price, Task>? OnPriceUpdate;
    public event Func<ExchangeOrderUpdate, Task>? OnExchangeOrderUpdate;
    public event Func<ExchangeAccountUpdate, Task>? OnExchangeAccountUpdate;

    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }

    public Task StartAsync(CancellationToken ct = default) { StartCalls++; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default) { StopCalls++; return Task.CompletedTask; }
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;
    public Task SubscribeKlinesAsync(Symbol symbol, KlineInterval interval, CancellationToken ct = default) => Task.CompletedTask;
    public Task SubscribeMarkPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.CompletedTask;
    public Task UnsubscribeAsync(Symbol symbol, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void TouchEvents()
    {
        OnPriceUpdate?.Invoke(default!, default!);
    }

    public async Task FireKlineUpdateAsync(Symbol symbol, KlineInterval interval, Kline kline)
    {
        var handlers = OnKlineUpdate;
        if (handlers is null) return;

        foreach (Func<Symbol, KlineInterval, Kline, Task> h
                 in handlers.GetInvocationList().Cast<Func<Symbol, KlineInterval, Kline, Task>>())
        {
            await h(symbol, interval, kline).ConfigureAwait(false);
        }

        _ = (Action)TouchEvents;
    }

    public async Task FireOrderUpdateAsync(ExchangeOrderUpdate update)
    {
        var handler = OnExchangeOrderUpdate;
        if (handler is null) return;
        foreach (Func<ExchangeOrderUpdate, Task> h
                 in handler.GetInvocationList().Cast<Func<ExchangeOrderUpdate, Task>>())
        {
            await h(update).ConfigureAwait(false);
        }
    }

    public async Task FireAccountUpdateAsync(ExchangeAccountUpdate update)
    {
        var handler = OnExchangeAccountUpdate;
        if (handler is null) return;
        foreach (Func<ExchangeAccountUpdate, Task> h
                 in handler.GetInvocationList().Cast<Func<ExchangeAccountUpdate, Task>>())
        {
            await h(update).ConfigureAwait(false);
        }
    }
}

internal sealed class FakeExchangeClient : IExchangeClient
{
    public decimal Balance { get; set; }
    public IReadOnlyList<Kline> PreloadKlines { get; set; } = Array.Empty<Kline>();
    public int PlaceOrderCalls { get; private set; }
    public string ExchangeName => "FAKE";
    public string QuoteAsset => "USDT";
    public TradingMode CurrentMode => TradingMode.Demo;
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;

    public Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default) =>
        Task.FromResult(Balance);
    public Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default) =>
        Task.FromResult(Balance);
    public Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Kline>> GetKlinesAsync(
        Symbol symbol, KlineInterval interval, int limit = 500,
        DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) =>
        Task.FromResult(PreloadKlines);

    public Task<Price> GetMarkPriceAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(Price.Create(100m));
    public Task<Price> GetSpotPriceAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(Price.Create(100m));
    public Task<MarketSnapshot> GetMarketSnapshotAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(MarketSnapshot.Create(symbol, DateTime.UtcNow,
            Price.Create(100m), Price.Create(99.5m), Price.Create(100.5m)));
    public Task<SymbolTradingRules> GetTradingRulesAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(new SymbolTradingRules(symbol, 0.001m, 1_000_000m, 0.001m, 0.01m, 5m, 20));

    public Task PlaceOrderAsync(Order order, CancellationToken ct = default)
    {
        PlaceOrderCalls++;
        order.AssignExchangeOrderId($"FAKE-{PlaceOrderCalls}");
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangePositionInfo>>(Array.Empty<ExchangePositionInfo>());
}

internal sealed class FakeStrategy : IStrategy
{
    private readonly SignalType _signal;
    public FakeStrategy(SignalType signalFor) => _signal = signalFor;
    public string StrategyType => "Fake";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config, IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot, IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        TradingSignal s = _signal switch
        {
            SignalType.OpenLong  => TradingSignal.OpenLong(config.Symbol, Price.Create(100m), Price.Create(98m), Price.Create(104m), 0.8m, "fake"),
            SignalType.OpenShort => TradingSignal.OpenShort(config.Symbol, Price.Create(100m), Price.Create(102m), Price.Create(96m), 0.8m, "fake"),
            SignalType.CloseLong => TradingSignal.CloseLong(config.Symbol, Price.Create(100m), "fake"),
            SignalType.CloseShort => TradingSignal.CloseShort(config.Symbol, Price.Create(100m), "fake"),
            _ => TradingSignal.None(config.Symbol, Price.Create(100m))
        };
        return Task.FromResult(s);
    }
}

internal sealed class FakeOrderRepository : IOrderRepository
{
    public List<Order> Added { get; } = new();
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
    public Task<Order?> GetByExchangeOrderIdAsync(string exchangeOrderId, CancellationToken ct = default) => Task.FromResult<Order?>(null);
    public Task<IReadOnlyList<Order>> GetActiveOrdersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
    public Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
    public Task<IReadOnlyList<Order>> GetByStrategyIdAsync(Guid strategyId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
    public Task AddAsync(Order order, CancellationToken ct = default) { Added.Add(order); return Task.CompletedTask; }
    public Task UpdateAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakePositionRepository : IPositionRepository
{
    public Task<Position?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Position?>(null);
    public Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Array.Empty<Position>());
    public Task<IReadOnlyList<Position>> GetOpenPositionsBySymbolAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Array.Empty<Position>());
    public Task<IReadOnlyList<Position>> GetByStrategyIdAsync(Guid strategyId, bool includeClosedPositions = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Array.Empty<Position>());
    public Task<IReadOnlyList<Position>> GetClosedPositionsInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Array.Empty<Position>());
    public Task AddAsync(Position position, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateAsync(Position position, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeStrategyRepository : IStrategyRepository
{
    public Task<Strategy?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Strategy?>(null);
    public Task<Strategy?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Strategy?>(null);
    public Task<IReadOnlyList<Strategy>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Strategy>>(Array.Empty<Strategy>());
    public Task<IReadOnlyList<Strategy>> GetByStatusAsync(StrategyStatus status, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Strategy>>(Array.Empty<Strategy>());
    public Task AddAsync(Strategy strategy, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateAsync(Strategy strategy, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveChangesCalls { get; private set; }
    public Task<int> SaveChangesAsync(CancellationToken ct = default) { SaveChangesCalls++; return Task.FromResult(1); }
    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
}
