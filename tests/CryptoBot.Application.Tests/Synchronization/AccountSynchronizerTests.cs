using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Synchronization;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Synchronization;

/// <summary>
/// S5 — AccountSynchronizer tests。
///
/// 為何不共用 StrategyEngineTests 的 FakeOrderRepository / FakePositionRepository：
/// 那兩個 stub 只回傳 null/empty，無法模擬「依 ExchangeOrderId 查得到本地 Order」的路徑，
/// 而 Synchronizer 的核心邏輯正是這條路徑。本檔內另寫一組有狀態的 fake。
/// </summary>
public class AccountSynchronizerTests
{
    private static readonly Symbol BTC = Symbol.Parse("BTC-USDT");

    // ─────────────── helpers ───────────────

    private static Order MakeLimitOrder(string exchangeId, decimal qty = 1m, decimal price = 100m)
    {
        var o = Order.CreateLimitOrder(
            BTC, OrderSide.Buy, PositionSide.Long,
            Quantity.Create(qty), Price.Create(price));
        o.AssignExchangeOrderId(exchangeId);
        return o;
    }

    private static Position MakeOpenPosition(PositionSide side = PositionSide.Long, decimal qty = 1m, decimal entry = 100m)
    {
        return Position.Open(
            BTC, side,
            Quantity.Create(qty), Price.Create(entry),
            Leverage.Moderate, MarginMode.Isolated);
    }

    private static FakeSyncServiceProvider BuildSp(
        StatefulOrderRepo? orderRepo = null,
        StatefulPositionRepo? positionRepo = null,
        CountingUnitOfWork? uow = null)
    {
        return new FakeSyncServiceProvider()
            .Register<IOrderRepository>(orderRepo ?? new StatefulOrderRepo())
            .Register<IPositionRepository>(positionRepo ?? new StatefulPositionRepo())
            .Register<IUnitOfWork>(uow ?? new CountingUnitOfWork());
    }

    // ─────────────── StartAsync / StopAsync ───────────────

    [Fact]
    public async Task StartAsync_SubscribesOnceEvenIfCalledTwice()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var sp = BuildSp();

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);

        await sut.StartAsync();
        await sut.StartAsync();  // 幂等

        Assert.Equal(1, market.OrderHandlerAttached);
        Assert.Equal(1, market.AccountHandlerAttached);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesHandlers()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var sp = BuildSp();

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);

        await sut.StartAsync();
        await sut.StopAsync();

        Assert.Equal(1, market.OrderHandlerDetached);
        Assert.Equal(1, market.AccountHandlerDetached);
    }

    // ─────────────── HandleOrderUpdate ───────────────

    [Fact]
    public async Task OrderUpdate_UnknownExchangeId_IgnoredWithoutSave()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var orderRepo = new StatefulOrderRepo();
        var uow = new CountingUnitOfWork();
        var sp = BuildSp(orderRepo: orderRepo, uow: uow);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        await market.FireOrderUpdateAsync(new ExchangeOrderUpdate(
            ExchangeOrderId: "NOPE",
            Symbol: BTC,
            Status: OrderStatus.Filled,
            Quantity: 1m, QuantityFilled: 1m,
            AverageFillPrice: 100m, Fee: 0m,
            UpdateTime: DateTime.UtcNow));

        Assert.Empty(orderRepo.Updated);
        Assert.Equal(0, uow.SaveChangesCalls);
    }

    [Fact]
    public async Task OrderUpdate_PartialFill_RecordsFillAndSaves()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var order = MakeLimitOrder("EX-1", qty: 2m);
        var orderRepo = new StatefulOrderRepo();
        orderRepo.Seed(order);
        var uow = new CountingUnitOfWork();
        var sp = BuildSp(orderRepo: orderRepo, uow: uow);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        await market.FireOrderUpdateAsync(new ExchangeOrderUpdate(
            ExchangeOrderId: "EX-1",
            Symbol: BTC,
            Status: OrderStatus.PartiallyFilled,
            Quantity: 2m, QuantityFilled: 1m,
            AverageFillPrice: 100m, Fee: 0.1m,
            UpdateTime: DateTime.UtcNow));

        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(1m, order.FilledQuantity.Value);
        Assert.Equal(1, uow.SaveChangesCalls);
    }

    [Fact]
    public async Task OrderUpdate_FullFill_StatusBecomesFilled()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var order = MakeLimitOrder("EX-2", qty: 1m);
        var orderRepo = new StatefulOrderRepo();
        orderRepo.Seed(order);
        var sp = BuildSp(orderRepo: orderRepo);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        await market.FireOrderUpdateAsync(new ExchangeOrderUpdate(
            ExchangeOrderId: "EX-2",
            Symbol: BTC,
            Status: OrderStatus.Filled,
            Quantity: 1m, QuantityFilled: 1m,
            AverageFillPrice: 100m, Fee: 0.05m,
            UpdateTime: DateTime.UtcNow));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(1m, order.FilledQuantity.Value);
    }

    [Fact]
    public async Task OrderUpdate_Canceled_TransitionsToCanceled()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var order = MakeLimitOrder("EX-3");
        var orderRepo = new StatefulOrderRepo();
        orderRepo.Seed(order);
        var sp = BuildSp(orderRepo: orderRepo);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        await market.FireOrderUpdateAsync(new ExchangeOrderUpdate(
            ExchangeOrderId: "EX-3",
            Symbol: BTC,
            Status: OrderStatus.Canceled,
            Quantity: 1m, QuantityFilled: 0m,
            AverageFillPrice: null, Fee: 0m,
            UpdateTime: DateTime.UtcNow));

        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public async Task OrderUpdate_HandlerExceptionIsSwallowed()
    {
        // 透過「拋例外的 repo」驗證 WS handler 不會把例外往上丟
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var sp = new FakeSyncServiceProvider()
            .Register<IOrderRepository>(new ThrowingOrderRepo())
            .Register<IUnitOfWork>(new CountingUnitOfWork())
            .Register<IPositionRepository>(new StatefulPositionRepo());

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        var ex = await Record.ExceptionAsync(() => market.FireOrderUpdateAsync(new ExchangeOrderUpdate(
            "EX-BOOM", BTC, OrderStatus.Filled, 1m, 1m, 100m, 0m, DateTime.UtcNow)));

        Assert.Null(ex);
    }

    // ─────────────── HandleAccountUpdate ───────────────

    [Fact]
    public async Task AccountUpdate_PositionQuantityZero_ClosesLocalPosition()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var localPos = MakeOpenPosition(PositionSide.Long, qty: 1m, entry: 100m);
        var positionRepo = new StatefulPositionRepo();
        positionRepo.Seed(localPos);
        var uow = new CountingUnitOfWork();
        var sp = BuildSp(positionRepo: positionRepo, uow: uow);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        await market.FireAccountUpdateAsync(new ExchangeAccountUpdate(
            UpdateTime: DateTime.UtcNow,
            Balances: Array.Empty<ExchangeBalanceEntry>(),
            Positions: new[]
            {
                new ExchangePositionInfo(BTC, PositionSide.Long,
                    Quantity: 0m, EntryPrice: 100m, MarkPrice: 105m,
                    UnrealizedPnL: 0m, LiquidationPrice: 0m, Leverage: 1)
            }));

        Assert.True(localPos.IsClosed);
        Assert.Equal(1, uow.SaveChangesCalls);
    }

    [Fact]
    public async Task AccountUpdate_PositionZeroWithoutMarkPrice_FallsBackToExchangeQuery()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient { MarkPrice = Price.Create(123m) };
        var localPos = MakeOpenPosition(PositionSide.Long, qty: 1m, entry: 100m);
        var positionRepo = new StatefulPositionRepo();
        positionRepo.Seed(localPos);
        var sp = BuildSp(positionRepo: positionRepo);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        await market.FireAccountUpdateAsync(new ExchangeAccountUpdate(
            UpdateTime: DateTime.UtcNow,
            Balances: Array.Empty<ExchangeBalanceEntry>(),
            Positions: new[]
            {
                new ExchangePositionInfo(BTC, PositionSide.Long,
                    Quantity: 0m, EntryPrice: 100m, MarkPrice: 0m,
                    UnrealizedPnL: 0m, LiquidationPrice: 0m, Leverage: 1)
            }));

        Assert.True(localPos.IsClosed);
        Assert.Equal(1, exchange.GetMarkPriceCalls);
    }

    [Fact]
    public async Task AccountUpdate_PositionOpenWithMarkPrice_UpdatesCurrentPrice()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var localPos = MakeOpenPosition(PositionSide.Long, qty: 1m, entry: 100m);
        var positionRepo = new StatefulPositionRepo();
        positionRepo.Seed(localPos);
        var sp = BuildSp(positionRepo: positionRepo);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.StartAsync();

        await market.FireAccountUpdateAsync(new ExchangeAccountUpdate(
            UpdateTime: DateTime.UtcNow,
            Balances: Array.Empty<ExchangeBalanceEntry>(),
            Positions: new[]
            {
                new ExchangePositionInfo(BTC, PositionSide.Long,
                    Quantity: 1m, EntryPrice: 100m, MarkPrice: 101.5m,
                    UnrealizedPnL: 1.5m, LiquidationPrice: 0m, Leverage: 1)
            }));

        Assert.False(localPos.IsClosed);
        Assert.NotNull(localPos.CurrentPrice);
        Assert.Equal(101.5m, localPos.CurrentPrice.Value);
    }

    // ─────────────── ReconcileAsync ───────────────

    [Fact]
    public async Task Reconcile_RefreshesActiveOrders()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var o1 = MakeLimitOrder("EX-R1");
        var o2 = MakeLimitOrder("EX-R2");
        var orderRepo = new StatefulOrderRepo();
        orderRepo.Seed(o1); orderRepo.Seed(o2);
        var sp = BuildSp(orderRepo: orderRepo);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.ReconcileAsync();

        Assert.Equal(2, exchange.RefreshOrderStatusCalls);
    }

    [Fact]
    public async Task Reconcile_ClosesOrphanLocalPosition()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient { MarkPrice = Price.Create(99m) };
        // 遠端回傳空 list → 本地那倉就是孤兒
        exchange.OpenPositions = Array.Empty<ExchangePositionInfo>();

        var orphan = MakeOpenPosition(PositionSide.Long, qty: 1m, entry: 100m);
        var positionRepo = new StatefulPositionRepo();
        positionRepo.Seed(orphan);
        var uow = new CountingUnitOfWork();
        var sp = BuildSp(positionRepo: positionRepo, uow: uow);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.ReconcileAsync();

        Assert.True(orphan.IsClosed);
        Assert.NotNull(orphan.CurrentPrice);
        Assert.Equal(99m, orphan.CurrentPrice.Value);
        Assert.Equal(1, uow.SaveChangesCalls);
    }

    [Fact]
    public async Task Reconcile_SkipsMatchedPosition()
    {
        var market = new SyncFakeMarketDataStream();
        var exchange = new SyncFakeExchangeClient();
        var local = MakeOpenPosition(PositionSide.Long, qty: 1m, entry: 100m);
        exchange.OpenPositions = new[]
        {
            new ExchangePositionInfo(BTC, PositionSide.Long,
                Quantity: 1m, EntryPrice: 100m, MarkPrice: 101m,
                UnrealizedPnL: 1m, LiquidationPrice: 0m, Leverage: 1)
        };
        var positionRepo = new StatefulPositionRepo();
        positionRepo.Seed(local);
        var sp = BuildSp(positionRepo: positionRepo);

        var sut = new AccountSynchronizer(market, exchange, sp, NullLogger<AccountSynchronizer>.Instance);
        await sut.ReconcileAsync();

        Assert.False(local.IsClosed);
    }
}

// ═════════════════════════════════════════════════════════
// Synchronization 專用的 stateful fakes
// ═════════════════════════════════════════════════════════

internal sealed class FakeSyncServiceProvider : IServiceProvider, IServiceScope, IServiceScopeFactory
{
    private readonly Dictionary<Type, object> _map = new();

    public FakeSyncServiceProvider Register<T>(T instance) where T : class
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

internal sealed class SyncFakeMarketDataStream : IMarketDataStream
{
    private Func<ExchangeOrderUpdate, Task>? _orderHandler;
    private Func<ExchangeAccountUpdate, Task>? _accountHandler;

    public int OrderHandlerAttached { get; private set; }
    public int OrderHandlerDetached { get; private set; }
    public int AccountHandlerAttached { get; private set; }
    public int AccountHandlerDetached { get; private set; }

    public event Func<Symbol, KlineInterval, CryptoBot.Domain.Aggregates.MarketDataAggregate.Kline, Task>? OnKlineUpdate;
    public event Func<Symbol, Price, Task>? OnPriceUpdate;

    public event Func<ExchangeOrderUpdate, Task>? OnExchangeOrderUpdate
    {
        add { _orderHandler = (Func<ExchangeOrderUpdate, Task>?)Delegate.Combine(_orderHandler, value); OrderHandlerAttached++; }
        remove { _orderHandler = (Func<ExchangeOrderUpdate, Task>?)Delegate.Remove(_orderHandler, value); OrderHandlerDetached++; }
    }

    public event Func<ExchangeAccountUpdate, Task>? OnExchangeAccountUpdate
    {
        add { _accountHandler = (Func<ExchangeAccountUpdate, Task>?)Delegate.Combine(_accountHandler, value); AccountHandlerAttached++; }
        remove { _accountHandler = (Func<ExchangeAccountUpdate, Task>?)Delegate.Remove(_accountHandler, value); AccountHandlerDetached++; }
    }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;
    public Task SubscribeKlinesAsync(Symbol s, KlineInterval i, CancellationToken ct = default) => Task.CompletedTask;
    public Task SubscribeMarkPriceAsync(Symbol s, CancellationToken ct = default) => Task.CompletedTask;
    public Task UnsubscribeAsync(Symbol s, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task FireOrderUpdateAsync(ExchangeOrderUpdate update) =>
        _orderHandler?.Invoke(update) ?? Task.CompletedTask;

    public Task FireAccountUpdateAsync(ExchangeAccountUpdate update) =>
        _accountHandler?.Invoke(update) ?? Task.CompletedTask;

    private void TouchUnused()
    {
        OnKlineUpdate?.Invoke(default!, default!, default!);
        OnPriceUpdate?.Invoke(default!, default!);
    }
}

internal sealed class SyncFakeExchangeClient : IExchangeClient
{
    public string ExchangeName => "SYNCFAKE";
    public string QuoteAsset => "USDT";
    public TradingMode CurrentMode => TradingMode.Demo;
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;
    public Price MarkPrice { get; set; } = Price.Create(100m);
    public IReadOnlyList<ExchangePositionInfo> OpenPositions { get; set; } = Array.Empty<ExchangePositionInfo>();
    public int RefreshOrderStatusCalls { get; private set; }
    public int GetMarkPriceCalls { get; private set; }

    public Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default) => Task.FromResult(0m);
    public Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<CryptoBot.Domain.Aggregates.MarketDataAggregate.Kline>> GetKlinesAsync(
        Symbol symbol, KlineInterval interval, int limit = 500,
        DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CryptoBot.Domain.Aggregates.MarketDataAggregate.Kline>>(Array.Empty<CryptoBot.Domain.Aggregates.MarketDataAggregate.Kline>());

    public Task<Price> GetMarkPriceAsync(Symbol symbol, CancellationToken ct = default)
    {
        GetMarkPriceCalls++;
        return Task.FromResult(MarkPrice);
    }
    public Task<Price> GetSpotPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(MarkPrice);

    public Task<CryptoBot.Domain.Aggregates.MarketDataAggregate.MarketSnapshot> GetMarketSnapshotAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(CryptoBot.Domain.Aggregates.MarketDataAggregate.MarketSnapshot.Create(
            symbol, DateTime.UtcNow,
            MarkPrice, Price.Create(MarkPrice.Value - 0.5m), Price.Create(MarkPrice.Value + 0.5m)));

    public Task<SymbolTradingRules> GetTradingRulesAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(new SymbolTradingRules(symbol, 0.001m, 1_000_000m, 0.001m, 0.01m, 5m, 20));

    public Task PlaceOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;

    public Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default)
    {
        RefreshOrderStatusCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult(OpenPositions);
}

internal sealed class StatefulOrderRepo : IOrderRepository
{
    private readonly Dictionary<string, Order> _byExchangeId = new();
    public List<Order> Updated { get; } = new();

    public void Seed(Order order)
    {
        if (order.ExchangeOrderId is null) throw new InvalidOperationException("Must assign exchange id before seeding.");
        _byExchangeId[order.ExchangeOrderId] = order;
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<Order?>(_byExchangeId.Values.FirstOrDefault(o => o.Id == id));

    public Task<Order?> GetByExchangeOrderIdAsync(string exchangeOrderId, CancellationToken ct = default) =>
        Task.FromResult(_byExchangeId.TryGetValue(exchangeOrderId, out var o) ? o : null);

    public Task<IReadOnlyList<Order>> GetActiveOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_byExchangeId.Values.Where(o => o.IsActive).ToList());

    public Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_byExchangeId.Values.Where(o => o.Symbol.Equals(symbol)).ToList());

    public Task<IReadOnlyList<Order>> GetByStrategyIdAsync(Guid strategyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_byExchangeId.Values.Where(o => o.StrategyId == strategyId).ToList());

    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_byExchangeId.Values.OrderByDescending(o => o.CreatedAt).Take(limit).ToList());

    public Task AddAsync(Order order, CancellationToken ct = default)
    {
        if (order.ExchangeOrderId is not null) _byExchangeId[order.ExchangeOrderId] = order;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        Updated.Add(order);
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingOrderRepo : IOrderRepository
{
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task<Order?> GetByExchangeOrderIdAsync(string exchangeOrderId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task<IReadOnlyList<Order>> GetActiveOrdersAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task<IReadOnlyList<Order>> GetByStrategyIdAsync(Guid strategyId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task AddAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StatefulPositionRepo : IPositionRepository
{
    private readonly List<Position> _store = new();

    public void Seed(Position p) => _store.Add(p);

    public Task<Position?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<Position?>(_store.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(_store.Where(p => !p.IsClosed).ToList());

    public Task<IReadOnlyList<Position>> GetOpenPositionsBySymbolAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(_store.Where(p => !p.IsClosed && p.Symbol.Equals(symbol)).ToList());

    public Task<IReadOnlyList<Position>> GetByStrategyIdAsync(Guid strategyId, bool includeClosedPositions = false, CancellationToken ct = default)
    {
        var q = _store.Where(p => p.StrategyId == strategyId);
        if (!includeClosedPositions) q = q.Where(p => !p.IsClosed);
        return Task.FromResult<IReadOnlyList<Position>>(q.ToList());
    }

    public Task<IReadOnlyList<Position>> GetClosedPositionsInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(_store.Where(p => p.IsClosed && p.ClosedAt >= from && p.ClosedAt <= to).ToList());

    public Task AddAsync(Position position, CancellationToken ct = default) { _store.Add(position); return Task.CompletedTask; }
    public Task UpdateAsync(Position position, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class CountingUnitOfWork : IUnitOfWork
{
    public int SaveChangesCalls { get; private set; }
    public Task<int> SaveChangesAsync(CancellationToken ct = default) { SaveChangesCalls++; return Task.FromResult(1); }
    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
}
