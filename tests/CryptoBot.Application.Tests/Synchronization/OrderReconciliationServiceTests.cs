using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Synchronization;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Synchronization;

/// <summary>
/// S66-B：對帳服務行為契約測試。
/// 用 FakeTimeProvider 推進「人造時間」直接測試各分支，不依賴真實 PeriodicTimer 或網路。
/// </summary>
public class OrderReconciliationServiceTests
{
    private static readonly Symbol Btc = Symbol.Parse("BTC-USDT");
    private static readonly DateTime T0 = new(2026, 4, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FreshPendingOrder_NotTouched()
    {
        // Arrange — 訂單剛建立 30 秒，PendingAssignThreshold 是 1 分鐘，不該動
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_aaaa1111", createdAt: T0);
        repo.Seed(order);
        clock.SetNow(T0.AddSeconds(30));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Null(order.ExchangeOrderId);
        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Equal(0, exchange.GetByCidCalls);
        Assert.Equal(0, uow.SaveChangesCalls);
        // S66-B HOTFIX：未變更的訂單**不該**被 UpdateAsync
        Assert.Empty(repo.UpdatedOrders);
    }

    [Fact]
    public async Task A1_PendingOrderFoundOnExchange_RebindsExchangeOrderId()
    {
        // Arrange — 訂單已 2 分鐘，交易所有此 cid 且 status=New
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_aaaa1111", createdAt: T0);
        repo.Seed(order);
        exchange.SetSnapshot("cb_strat_aaaa1111", new ExchangeOrderSnapshot(
            ExchangeOrderId: "EX-12345",
            ClientOrderId: "cb_strat_aaaa1111",
            Symbol: Btc,
            Side: OrderSide.Buy,
            PositionSide: PositionSide.Long,
            Status: OrderStatus.New,
            Quantity: 0.01m,
            QuantityFilled: 0m,
            AveragePrice: null,
            UpdateTime: T0));
        clock.SetNow(T0.AddMinutes(2));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal("EX-12345", order.ExchangeOrderId);
        Assert.Equal(OrderStatus.New, order.Status);  // remote 還是 New
        Assert.Equal(1, uow.SaveChangesCalls);
        // S66-B HOTFIX：A1 補回 ExchangeOrderId 也屬於 mutation，必呼叫 UpdateAsync
        Assert.Single(repo.UpdatedOrders);
    }

    [Fact]
    public async Task A1_PendingFoundAsFilled_RecordsFillAndCommits()
    {
        // Arrange — 訂單已 2 分鐘，交易所端已成交（典型「下單成功但本地崩前沒寫 ExchangeOrderId」）
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_filled001", createdAt: T0, qty: 0.01m);
        repo.Seed(order);
        exchange.SetSnapshot("cb_strat_filled001", new ExchangeOrderSnapshot(
            ExchangeOrderId: "EX-99999",
            ClientOrderId: "cb_strat_filled001",
            Symbol: Btc,
            Side: OrderSide.Buy,
            PositionSide: PositionSide.Long,
            Status: OrderStatus.Filled,
            Quantity: 0.01m,
            QuantityFilled: 0.01m,
            AveragePrice: 50000m,
            UpdateTime: T0));
        clock.SetNow(T0.AddMinutes(2));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal("EX-99999", order.ExchangeOrderId);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0.01m, order.FilledQuantity.Value);
        Assert.Equal(50000m, order.AverageFillPrice?.Value);
        Assert.Single(repo.UpdatedOrders);
    }

    [Fact]
    public async Task A2_PendingOrphanOver5Min_GetsRejected()
    {
        // Arrange — 訂單 6 分鐘了，交易所完全找不到（probably crashed before SDK call）
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_orphan", createdAt: T0);
        repo.Seed(order);
        // exchange.SetSnapshot 不設 → 預設回 null
        clock.SetNow(T0.AddMinutes(6));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Contains("Orphan pending cleanup", order.RejectReason);
        Assert.Equal(1, uow.SaveChangesCalls);
        // S66-B HOTFIX：mutated 訂單**必須**被 UpdateAsync 顯式註記
        Assert.Single(repo.UpdatedOrders);
        Assert.Same(order, repo.UpdatedOrders[0]);
    }

    [Fact]
    public async Task A_PendingNotFoundUnder5Min_KeptForRetry()
    {
        // Arrange — 訂單 3 分鐘，交易所還沒看到（網路延遲？）— 給它再等等
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_waiting", createdAt: T0);
        repo.Seed(order);
        clock.SetNow(T0.AddMinutes(3));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Null(order.RejectReason);
        Assert.Equal(0, uow.SaveChangesCalls);
    }

    [Fact]
    public async Task B_ZombieWithExchangeIdOver5Min_TriggersRefresh()
    {
        // Arrange — 訂單已有 ExchangeOrderId，但 Status 仍 New 達 6 分鐘（WS 漏接 fill）
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_zombie", createdAt: T0, qty: 0.02m);
        order.AssignExchangeOrderId("EX-ZOMBIE-1");
        repo.Seed(order);
        // 設 fake exchange.RefreshOrderStatusAsync 模擬「遠端已 Filled」
        exchange.RefreshBehaviour = (o) =>
        {
            o.RecordFill(Quantity.Create(0.02m), Price.Create(50500m), commission: 1m);
        };
        clock.SetNow(T0.AddMinutes(6));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(1, exchange.RefreshCalls);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0.02m, order.FilledQuantity.Value);
        Assert.Single(repo.UpdatedOrders);
    }

    [Fact]
    public async Task B_ZombieUnder5Min_NotTouched()
    {
        // Arrange — 訂單已有 ExchangeOrderId，Status 仍 New，但只有 4 分鐘
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_recent_active", createdAt: T0);
        order.AssignExchangeOrderId("EX-FRESH-1");
        repo.Seed(order);
        clock.SetNow(T0.AddMinutes(4));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(0, exchange.RefreshCalls);
        Assert.Equal(0, uow.SaveChangesCalls);
    }

    [Fact]
    public async Task ExchangeApiFailure_DoesNotCrashTick()
    {
        // Arrange — 訂單應對帳，但交易所 API 拋例外
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_apifail", createdAt: T0);
        repo.Seed(order);
        exchange.GetByCidThrows = new Exception("simulated network failure");
        clock.SetNow(T0.AddMinutes(2));

        // Act + Assert：tick 不該拋出
        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Null(order.ExchangeOrderId);
        Assert.Equal(0, uow.SaveChangesCalls);
        Assert.Empty(repo.UpdatedOrders);  // API 失敗 → ProcessOneAsync 回 false → 不該 UpdateAsync
    }

    [Fact]
    public async Task EmptyActiveList_NoErrors()
    {
        var (svc, _, _) = BuildSut(out var clock, out var uow);
        clock.SetNow(T0.AddMinutes(10));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(0, uow.SaveChangesCalls);
    }

    [Fact]
    public async Task RowsAffectedMismatch_EmitsWarning()
    {
        // S66-B HOTFIX 偵測器：當 SaveChanges 真的回 0（EF 沒抓到 mutation）時，
        // 必須有 LogWarning 浮上水面，而不是讓事故無聲蔓延。
        var clock = new FakeReconcileClock();
        var exchange = new FakeExchange();
        var repo = new ReconcileFakeOrderRepo();
        var uow = new RecordingUnitOfWork { RowsAffectedToReturn = 0 };  // 模擬 tracker 失靈
        var sp = new ReconcileFakeServiceProvider()
            .Register<IOrderRepository>(repo)
            .Register<IUnitOfWork>(uow);
        var loggerSpy = new SpyLogger<OrderReconciliationService>();
        var svc = new OrderReconciliationService(sp, exchange, loggerSpy, clock);

        var order = MakePendingOrder(cid: "cb_strat_mismatch", createdAt: T0);
        repo.Seed(order);
        clock.SetNow(T0.AddMinutes(6));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        // changed=1（A2 reject 了），但 RowsAffected=0 → 應觸發 mismatch warning
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(1, uow.SaveChangesCalls);
        Assert.Contains(loggerSpy.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("DB persistence mismatch"));
    }

    [Fact]
    public async Task PartiallyFilledOrder_GetsRefreshedAfterZombieThreshold()
    {
        // S72：擴大涵蓋 PartiallyFilled。S71 揪出 3 筆 LINK-USDT PartiallyFilled 卡 8 天的真因為
        // 原版本只處理 New + WS 漏接最後 1% fill update。本服務現在會對 PartiallyFilled + age > 5min
        // 的 Order 強制 RefreshOrderStatusAsync，避免長時間殭屍占用風控額度。
        var (svc, exchange, repo) = BuildSut(out var clock, out var uow);
        var order = MakePendingOrder(cid: "cb_strat_partial", createdAt: T0, qty: 0.02m);
        order.AssignExchangeOrderId("EX-PARTIAL-1");
        order.RecordFill(Quantity.Create(0.01m), Price.Create(50000m), commission: 0m);
        // 此時 Status = PartiallyFilled
        repo.Seed(order);
        // age = 10min > ZombieRefreshThreshold (5min) → 應觸發 RefreshOrderStatusAsync
        clock.SetNow(T0.AddMinutes(10));

        await svc.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(1, exchange.RefreshCalls);
    }

    // ============== Helpers ==============

    private static (OrderReconciliationService svc, FakeExchange exchange, ReconcileFakeOrderRepo repo) BuildSut(
        out FakeReconcileClock clock, out RecordingUnitOfWork uow)
    {
        clock = new FakeReconcileClock();
        var exchange = new FakeExchange();
        var repo = new ReconcileFakeOrderRepo();
        uow = new RecordingUnitOfWork();
        var sp = new ReconcileFakeServiceProvider()
            .Register<IOrderRepository>(repo)
            .Register<IUnitOfWork>(uow);
        var svc = new OrderReconciliationService(
            scopeFactory: sp,
            exchange: exchange,
            logger: NullLogger<OrderReconciliationService>.Instance,
            clock: clock);
        return (svc, exchange, repo);
    }

    private static Order MakePendingOrder(string cid, DateTime createdAt, decimal qty = 0.001m)
    {
        var o = Order.CreateLimitOrder(
            symbol: Btc,
            side: OrderSide.Buy,
            positionSide: PositionSide.Long,
            quantity: Quantity.Create(qty),
            limitPrice: Price.Create(40000m),
            strategyId: null,
            clientOrderId: cid);
        // CreatedAt 是 private set — 用 reflection 把它倒回測試需要的時間，避免被 DateTime.UtcNow 污染
        var prop = typeof(Order).GetProperty(nameof(Order.CreatedAt))!;
        prop.SetValue(o, createdAt);
        return o;
    }
}

// ============== Test fakes (檔內 internal, 不污染其他測試) ==============

internal sealed class FakeReconcileClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void SetNow(DateTime dt) => _now = new DateTimeOffset(dt, TimeSpan.Zero);
}

internal sealed class FakeExchange : IExchangeClient
{
    private readonly Dictionary<string, ExchangeOrderSnapshot> _byCid = new();

    public Exception? GetByCidThrows { get; set; }
    public Action<Order>? RefreshBehaviour { get; set; }
    public int GetByCidCalls { get; private set; }
    public int RefreshCalls { get; private set; }

    public void SetSnapshot(string cid, ExchangeOrderSnapshot snapshot) => _byCid[cid] = snapshot;

    public Task<ExchangeOrderSnapshot?> GetOrderByClientOrderIdAsync(
        Symbol symbol, string clientOrderId, CancellationToken ct = default)
    {
        GetByCidCalls++;
        if (GetByCidThrows is not null) throw GetByCidThrows;
        return Task.FromResult(_byCid.TryGetValue(clientOrderId, out var s) ? s : null);
    }

    public Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default)
    {
        RefreshCalls++;
        RefreshBehaviour?.Invoke(order);
        return Task.CompletedTask;
    }

    // ===== 其餘介面方法 — 本服務只用上面兩個，其餘給最小 stub =====
    public string ExchangeName => "Fake";
    public string QuoteAsset => "VST";
    public TradingMode CurrentMode => TradingMode.Demo;
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;
    public Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default) => Task.FromResult(0m);
    public Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Kline>> GetKlinesAsync(Symbol symbol, KlineInterval interval, int limit = 500, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Kline>>(Array.Empty<Kline>());
    public Task<Price> GetMarkPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(Price.Create(50000m));
    public Task<Price> GetSpotPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(Price.Create(50000m));
    public Task<MarketSnapshot> GetMarketSnapshotAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(MarketSnapshot.Create(
            symbol, DateTime.UtcNow,
            Price.Create(50000m), Price.Create(50000m), Price.Create(50000m)));
    public Task<SymbolTradingRules> GetTradingRulesAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(new SymbolTradingRules(symbol, 0.0001m, decimal.MaxValue, 0.0001m, 0.1m, 5m, 125));
    public Task PlaceOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ExchangeOpenOrderInfo>> GetOpenOrdersAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeOpenOrderInfo>>(Array.Empty<ExchangeOpenOrderInfo>());
    public Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangePositionInfo>>(Array.Empty<ExchangePositionInfo>());
    public Task<IReadOnlyList<ExchangeTradeInfo>> GetTradeHistoryAsync(Symbol symbol, DateTime since, DateTime? until = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeTradeInfo>>(Array.Empty<ExchangeTradeInfo>());
    public Task<DateTime> GetServerTimeAsync(CancellationToken ct = default) =>
        Task.FromResult(DateTime.UtcNow);
}

internal sealed class ReconcileFakeOrderRepo : IOrderRepository
{
    private readonly List<Order> _store = new();

    /// <summary>S66-B HOTFIX 測試用：記錄 UpdateAsync 被呼叫的訂單清單，用於驗證對帳服務有顯式註記變更。</summary>
    public List<Order> UpdatedOrders { get; } = new();

    public void Seed(Order order) => _store.Add(order);

    public Task<IReadOnlyList<Order>> GetActiveOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_store.Where(o => o.IsActive).ToList());

    // 本測試只用 GetActiveOrdersAsync；其餘給 stub
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.FirstOrDefault(o => o.Id == id));
    public Task<Order?> GetByExchangeOrderIdAsync(string exchangeOrderId, CancellationToken ct = default) =>
        Task.FromResult(_store.FirstOrDefault(o => o.ExchangeOrderId == exchangeOrderId));
    public Task<Order?> GetByClientOrderIdAsync(string clientOrderId, CancellationToken ct = default) =>
        Task.FromResult(_store.FirstOrDefault(o => o.ClientOrderId == clientOrderId));
    public Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_store.Where(o => o.Symbol.Equals(symbol)).ToList());
    public Task<IReadOnlyList<Order>> GetByStrategyIdAsync(Guid strategyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_store.Where(o => o.StrategyId == strategyId).ToList());
    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_store.OrderByDescending(o => o.CreatedAt).Take(limit).ToList());
    public Task AddAsync(Order order, CancellationToken ct = default) { _store.Add(order); return Task.CompletedTask; }
    public Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        UpdatedOrders.Add(order);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingUnitOfWork : IUnitOfWork
{
    public int SaveChangesCalls { get; private set; }

    /// <summary>S66-B HOTFIX 測試用：可控 SaveChanges 回傳的 rowsAffected，模擬 EF tracker 失靈場景。</summary>
    public int RowsAffectedToReturn { get; set; } = 1;

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.FromResult(RowsAffectedToReturn);
    }
    public Task<int> SaveChangesWithRetryAsync(int maxAttempts = 3, CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.FromResult(RowsAffectedToReturn);
    }
    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class ReconcileFakeServiceProvider : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    private readonly Dictionary<Type, object> _map = new();

    public ReconcileFakeServiceProvider Register<T>(T instance) where T : class
    {
        _map[typeof(T)] = instance;
        return this;
    }

    public IServiceScope CreateScope() => this;
    public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    public IServiceProvider ServiceProvider => this;
    public void Dispose() { }
}

internal sealed class SpyLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
