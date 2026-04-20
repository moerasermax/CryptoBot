using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Notifications;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.Application.Strategies.SmaCrossover;
using CryptoBot.Application.Synchronization;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Integration;

/// <summary>
/// S7 全線試車整合測試 — 把整條管線串起來跑一次：
///
///   預設策略（Running in repo）
///      ↓ StrategyRuntimeHostedService.StartAsync
///   載入策略 → 造 Executor → 預載歷史 K 線
///      ↓ IMarketDataStream.OnKlineUpdate（我們手動 Fire）
///   SmaCrossoverStrategy.AnalyzeAsync → OpenLong 訊號
///      ↓
///   OrderSizer → RiskManager → IExchangeClient.PlaceOrderAsync → IOrderRepository.AddAsync
///      ↓ IMarketDataStream.OnExchangeOrderUpdate（我們手動 Fire）
///   AccountSynchronizer → Order 狀態同步寫回
///
/// 這個測試不碰 EF Core、BingX SDK、Serilog — 用 in-memory fake 餵資料 / 驗結果。
/// 目的是證明應用層以上的組件全部能正確組合運作。
/// </summary>
public class S7TestDriveIntegrationTests
{
    private static readonly Symbol BTC = Symbol.Parse("BTC-USDT");

    [Fact]
    public async Task FullPipeline_GoldenCrossKline_TriggersSignalAndPersistsOrder()
    {
        await using var fixture = await PipelineFixture.BuildAsync();

        // Act — 模擬一根新收盤 K 線（產生黃金交叉）
        await fixture.Market.FireKlineUpdateAsync(
            BTC, KlineInterval.FifteenMinutes,
            fixture.TriggeringKline);

        // Assert — 下單成功、訂單寫入 repo、冷卻已記錄
        Assert.Equal(1, fixture.Exchange.PlaceOrderCalls);
        Assert.Single(fixture.OrderRepo.Store);

        var order = fixture.OrderRepo.Store.Values.Single();
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(PositionSide.Long, order.PositionSide);
        Assert.Equal(BTC, order.Symbol);
        Assert.NotNull(order.ExchangeOrderId);
        Assert.True(fixture.Cooldown.IsInCooldown(
            fixture.Strategy.Id, fixture.Strategy.Configuration.CooldownPeriod));

        await fixture.Host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FullPipeline_OrderUpdateAfterFill_SynchronizerPersistsFilledStatus()
    {
        await using var fixture = await PipelineFixture.BuildAsync();

        // 1) 先觸發下單讓 repo 裡有一筆 order
        await fixture.Market.FireKlineUpdateAsync(
            BTC, KlineInterval.FifteenMinutes, fixture.TriggeringKline);
        var order = fixture.OrderRepo.Store.Values.Single();

        // 2) 模擬 user-data WS 的 Filled 事件
        await fixture.Market.FireOrderUpdateAsync(new ExchangeOrderUpdate(
            ExchangeOrderId: order.ExchangeOrderId!,
            Symbol: BTC,
            Status: OrderStatus.Filled,
            Quantity: order.Quantity.Value,
            QuantityFilled: order.Quantity.Value,
            AverageFillPrice: 100m,
            Fee: 0.1m,
            UpdateTime: DateTime.UtcNow));

        // Assert — 本地訂單被同步成 Filled
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(order.Quantity.Value, order.FilledQuantity.Value);
        Assert.True(fixture.Uow.SaveChangesCalls >= 2);  // 下單 + 同步各一次

        await fixture.Host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedService_LoadsSeededStrategyAtStartup()
    {
        await using var fixture = await PipelineFixture.BuildAsync();

        Assert.Equal(1, fixture.Host.ExecutorCount);
        // StartAsync 會被 HostedService + 每個 Executor 各呼叫一次（StartAsync 必須冪等）
        Assert.True(fixture.Market.StartCalls >= 1);

        await fixture.Host.StopAsync(CancellationToken.None);
    }

    // ═════════════════════════════════════════════════════════
    // Fixture — 建一組完整的 in-memory 管線
    // ═════════════════════════════════════════════════════════

    private sealed class PipelineFixture : IAsyncDisposable
    {
        public required StrategyRuntimeHostedService Host { get; init; }
        public required PipelineFakeMarketDataStream Market { get; init; }
        public required PipelineFakeExchangeClient Exchange { get; init; }
        public required PipelineInMemoryOrderRepo OrderRepo { get; init; }
        public required PipelineInMemoryUnitOfWork Uow { get; init; }
        public required IStrategyCooldownTracker Cooldown { get; init; }
        public required Strategy Strategy { get; init; }
        public required IAccountSynchronizer Sync { get; init; }
        public required Kline TriggeringKline { get; init; }
        public required ServiceProvider ServiceProvider { get; init; }

        public static async Task<PipelineFixture> BuildAsync()
        {
            var services = new ServiceCollection();

            // 基礎建設 / 假件
            var market = new PipelineFakeMarketDataStream();
            var exchange = new PipelineFakeExchangeClient
            {
                Balance = 100_000m,
                PreloadedKlines = BuildHistoricalKlines()  // 59 根歷史
            };
            var orderRepo = new PipelineInMemoryOrderRepo();
            var positionRepo = new PipelineInMemoryPositionRepo();
            var strategyRepo = new PipelineInMemoryStrategyRepo();
            var uow = new PipelineInMemoryUnitOfWork();

            services.AddSingleton<IMarketDataStream>(market);
            services.AddSingleton<IExchangeClient>(exchange);
            services.AddSingleton<IOrderRepository>(orderRepo);
            services.AddSingleton<IPositionRepository>(positionRepo);
            services.AddSingleton<IStrategyRepository>(strategyRepo);
            services.AddSingleton<IUnitOfWork>(uow);
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            // Application 層實際組件（不走 AddApplication 因為只要 SMA 一支策略）
            services.AddSingleton(RiskLimits.Moderate);
            services.AddSingleton<IStrategyCooldownTracker, StrategyCooldownTracker>();
            services.AddScoped<IRiskManager, RiskManager>();
            services.AddScoped<IOrderSizer, OrderSizer>();
            services.AddSingleton<IStrategy, SmaCrossoverStrategy>();
            services.AddSingleton<IStrategyFactory, StrategyFactory>();
            services.AddSingleton<IStrategyExecutorFactory, StrategyExecutorFactory>();
            services.AddSingleton<IAccountSynchronizer, AccountSynchronizer>();
            services.AddSingleton<INotificationService, NoOpNotificationService>();
            services.AddSingleton<IRealtimeBroadcaster, NullRealtimeBroadcaster>();

            var sp = services.BuildServiceProvider();

            // Seed — 一筆 Running 狀態的 SmaCrossover 策略
            var strategy = Strategy.Create(
                "SMA-IntegrationTest",
                "SmaCrossover",
                StrategyConfiguration.Create(
                    BTC, KlineInterval.FifteenMinutes, Leverage.Create(3),
                    riskPerTradePercent: 0.02m, stopLossPercent: 0.02m, takeProfitPercent: 0.04m,
                    maxKlineWindow: 200,
                    parameters: new Dictionary<string, decimal>
                    {
                        ["FastSmaPeriod"] = 20,
                        ["SlowSmaPeriod"] = 50,
                    }));
            strategy.Start();
            await strategyRepo.AddAsync(strategy);

            var sync = sp.GetRequiredService<IAccountSynchronizer>();

            var host = new StrategyRuntimeHostedService(
                market, sync,
                sp.GetRequiredService<IStrategyExecutorFactory>(),
                sp.GetRequiredService<IStrategyFactory>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<StrategyRuntimeHostedService>.Instance);

            await host.StartAsync(CancellationToken.None);

            return new PipelineFixture
            {
                Host = host,
                Market = market,
                Exchange = exchange,
                OrderRepo = orderRepo,
                Uow = uow,
                Cooldown = sp.GetRequiredService<IStrategyCooldownTracker>(),
                Strategy = strategy,
                Sync = sync,
                TriggeringKline = BuildTriggeringKline(),
                ServiceProvider = sp,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await ServiceProvider.DisposeAsync();
        }
    }

    // ─────────────── 歷史 K 線構造 ───────────────

    /// <summary>
    /// 59 根歷史 K 線 — 前 50 根 @100，最後 9 根 @90，讓 SMA20 剛好壓在 SMA50 下方。
    /// 接下來的第 60 根（<see cref="BuildTriggeringKline"/>）跳到 300 就會黃金交叉。
    /// </summary>
    private static IReadOnlyList<Kline> BuildHistoricalKlines()
    {
        var list = new List<Kline>(59);
        var t = DateTime.UtcNow.AddMinutes(-60 * 15);
        for (int i = 0; i < 50; i++) { list.Add(MakeKline(t, 100m)); t = t.AddMinutes(15); }
        for (int i = 0; i < 9; i++)  { list.Add(MakeKline(t, 90m));  t = t.AddMinutes(15); }
        return list;
    }

    private static Kline BuildTriggeringKline()
    {
        var t = DateTime.UtcNow.AddMinutes(-15);
        return MakeKline(t, 300m);
    }

    private static Kline MakeKline(DateTime open, decimal close) =>
        Kline.Create(open, open.AddMinutes(14).AddSeconds(59),
            open: close, high: close + 0.5m, low: close - 0.5m, close: close,
            volume: 1m, interval: KlineInterval.FifteenMinutes);
}

// ═════════════════════════════════════════════════════════
// Pipeline-local fakes
// ═════════════════════════════════════════════════════════

internal sealed class PipelineFakeMarketDataStream : IMarketDataStream
{
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }

    public event Func<Symbol, KlineInterval, Kline, Task>? OnKlineUpdate;
    public event Func<Symbol, Price, Task>? OnPriceUpdate;
    public event Func<ExchangeOrderUpdate, Task>? OnExchangeOrderUpdate;
    public event Func<ExchangeAccountUpdate, Task>? OnExchangeAccountUpdate;

    public Task StartAsync(CancellationToken ct = default) { StartCalls++; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default) { StopCalls++; return Task.CompletedTask; }
    public Task SubscribeKlinesAsync(Symbol s, KlineInterval i, CancellationToken ct = default) => Task.CompletedTask;
    public Task SubscribeMarkPriceAsync(Symbol s, CancellationToken ct = default) => Task.CompletedTask;
    public Task UnsubscribeAsync(Symbol s, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task FireKlineUpdateAsync(Symbol s, KlineInterval i, Kline k) =>
        OnKlineUpdate?.Invoke(s, i, k) ?? Task.CompletedTask;

    public Task FireOrderUpdateAsync(ExchangeOrderUpdate u) =>
        OnExchangeOrderUpdate?.Invoke(u) ?? Task.CompletedTask;

    public Task FireAccountUpdateAsync(ExchangeAccountUpdate u) =>
        OnExchangeAccountUpdate?.Invoke(u) ?? Task.CompletedTask;

    private void TouchUnused() => OnPriceUpdate?.Invoke(default!, default!);
}

internal sealed class PipelineFakeExchangeClient : IExchangeClient
{
    public string ExchangeName => "PIPELINE-FAKE";
    public string QuoteAsset => "USDT";
    public decimal Balance { get; set; } = 100_000m;
    public IReadOnlyList<Kline> PreloadedKlines { get; set; } = Array.Empty<Kline>();
    public int PlaceOrderCalls { get; private set; }

    public Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default) =>
        Task.FromResult(Balance);
    public Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default) =>
        Task.FromResult(Balance);
    public Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Kline>> GetKlinesAsync(
        Symbol symbol, KlineInterval interval, int limit = 500,
        DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) =>
        Task.FromResult(PreloadedKlines);

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
        order.AssignExchangeOrderId($"EX-{PlaceOrderCalls:D4}");
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangePositionInfo>>(Array.Empty<ExchangePositionInfo>());
}

internal sealed class PipelineInMemoryOrderRepo : IOrderRepository
{
    public Dictionary<Guid, Order> Store { get; } = new();

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Store.TryGetValue(id, out var o) ? o : null);

    public Task<Order?> GetByExchangeOrderIdAsync(string exchangeOrderId, CancellationToken ct = default) =>
        Task.FromResult(Store.Values.FirstOrDefault(o => o.ExchangeOrderId == exchangeOrderId));

    public Task<IReadOnlyList<Order>> GetActiveOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(Store.Values.Where(o => o.IsActive).ToList());

    public Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(Store.Values.Where(o => o.Symbol.Equals(symbol)).ToList());

    public Task<IReadOnlyList<Order>> GetByStrategyIdAsync(Guid strategyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(Store.Values.Where(o => o.StrategyId == strategyId).ToList());

    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Order>>(Store.Values.OrderByDescending(o => o.CreatedAt).Take(limit).ToList());

    public Task AddAsync(Order order, CancellationToken ct = default) { Store[order.Id] = order; return Task.CompletedTask; }
    public Task UpdateAsync(Order order, CancellationToken ct = default) { Store[order.Id] = order; return Task.CompletedTask; }
}

internal sealed class PipelineInMemoryPositionRepo : IPositionRepository
{
    public List<Position> Store { get; } = new();

    public Task<Position?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<Position?>(Store.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Store.Where(p => !p.IsClosed).ToList());

    public Task<IReadOnlyList<Position>> GetOpenPositionsBySymbolAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Store.Where(p => !p.IsClosed && p.Symbol.Equals(symbol)).ToList());

    public Task<IReadOnlyList<Position>> GetByStrategyIdAsync(Guid strategyId, bool includeClosedPositions = false, CancellationToken ct = default)
    {
        var q = Store.Where(p => p.StrategyId == strategyId);
        if (!includeClosedPositions) q = q.Where(p => !p.IsClosed);
        return Task.FromResult<IReadOnlyList<Position>>(q.ToList());
    }

    public Task<IReadOnlyList<Position>> GetClosedPositionsInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Store.Where(p => p.IsClosed).ToList());

    public Task AddAsync(Position position, CancellationToken ct = default) { Store.Add(position); return Task.CompletedTask; }
    public Task UpdateAsync(Position position, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class PipelineInMemoryStrategyRepo : IStrategyRepository
{
    public List<Strategy> Store { get; } = new();

    public Task<Strategy?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<Strategy?>(Store.FirstOrDefault(s => s.Id == id));

    public Task<Strategy?> GetByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<Strategy?>(Store.FirstOrDefault(s => s.Name == name));

    public Task<IReadOnlyList<Strategy>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Strategy>>(Store.ToList());

    public Task<IReadOnlyList<Strategy>> GetByStatusAsync(StrategyStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Strategy>>(Store.Where(s => s.Status == status).ToList());

    public Task AddAsync(Strategy strategy, CancellationToken ct = default) { Store.Add(strategy); return Task.CompletedTask; }
    public Task UpdateAsync(Strategy strategy, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class PipelineInMemoryUnitOfWork : IUnitOfWork
{
    public int SaveChangesCalls { get; private set; }
    public Task<int> SaveChangesAsync(CancellationToken ct = default) { SaveChangesCalls++; return Task.FromResult(1); }
    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
}
