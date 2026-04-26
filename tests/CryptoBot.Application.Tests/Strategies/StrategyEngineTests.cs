using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Notifications;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.Application.Trading;
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

    // S99-T7 / S50：保證金上限防呆 — 風險預算算出的 qty 會把保證金撐爆時，
    // Sizer 應自動縮到 balance*leverage*0.95/entry 的可負擔規模（5% 緩衝），
    // 而不是把必定會被 BingX Rejected 的大單丟出去。
    [Fact]
    public async Task OrderSizer_CapsQuantityWhenMarginExceedsBalance()
    {
        // balance=1_000 (VST 真實規模)、leverage=Conservative(2x)、risk=10%（上限）、
        // entry=100、SL%=1%（下限之一，拿來放大風險預算）：
        //   riskAmount = 1_000 * 0.10 = 100；stopDist = 100 * 0.01 = 1 → rawQty = 100
        //   notional     = 100 * 100 = 10_000
        //   maxNotional  = 1_000 * 2 * 0.95 = 1_900 （S50 緩衝）
        //   notional > maxNotional → capped = 1_900 / 100 = 19 BTC
        //   stepSize 0.001 對齊後仍是 19。
        var exchange = new FakeExchangeClient { Balance = 1_000m };
        var sizer = new OrderSizer(exchange);
        var cfg = StrategyConfiguration.Create(
            symbol: Symbol.Parse("BTC-USDT"),
            interval: KlineInterval.FifteenMinutes,
            leverage: Leverage.Conservative, // 2x
            riskPerTradePercent: 0.10m,      // 10% (domain upper bound)
            stopLossPercent: 0.01m,
            takeProfitPercent: 0.04m);
        var strategy = MakeStrategy(cfg);
        var signal = TradingSignal.OpenLong(
            Symbol.Parse("BTC-USDT"), Price.Create(100m),
            Price.Create(99m), Price.Create(104m), 0.8m, "test");

        var qty = await sizer.ComputeAsync(strategy, signal);

        Assert.Equal(19m, qty.Value);
    }

    // S99-T7：如果帳戶太小、對齊後的 qty 低於交易所 minQuantity，Sizer 直接回 Zero，
    // Executor 的 zero-quantity 分支會 log + skip，絕不硬送碎步量。
    [Fact]
    public async Task OrderSizer_ReturnsZero_WhenAlignedBelowMinQuantity()
    {
        // FakeExchangeClient 回 minQuantity=0.001、stepSize=0.001、minNotional=5。
        // balance=1、entry=60_000、SL%=2% → stopDist=1_200；risk 2% → riskAmount=0.02
        // rawQty = 0.02/1200 ≈ 1.67e-5，floor 到 0.001 後 = 0 → < minQuantity 0.001 → Zero。
        var exchange = new FakeExchangeClient { Balance = 1m };
        var sizer = new OrderSizer(exchange);
        var strategy = MakeStrategy();
        var signal = TradingSignal.OpenLong(
            Symbol.Parse("BTC-USDT"), Price.Create(60_000m),
            Price.Create(58_800m), Price.Create(62_400m), 0.8m, "test");

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
            new DeterministicClientOrderIdGenerator(),
            NullLogger<StrategyExecutor>.Instance);

        await executor.StartAsync();

        // Act — 模擬一根新的收盤 K 線
        var newKline = MakeKline(DateTime.UtcNow, 100m, 101m);
        await marketData.FireKlineUpdateAsync(
            strategy.Configuration.Symbol, strategy.Configuration.Interval, newKline);

        // Assert — 下單 → 寫入 repo → 記錄冷卻
        Assert.Equal(1, exchange.PlaceOrderCalls);
        Assert.Single(orderRepo.Added);
        // S66-A T1.5：從 1 次 → 2 次。流程改為「先 AddAsync + SaveChanges(Pending) → 再 PlaceOrder
        //              → 再 SaveChanges(Filled + Position)」以根治「先交易所後 DB」的鬼單風險。
        Assert.Equal(2, unitOfWork.SaveChangesCalls);
        Assert.True(tracker.IsInCooldown(strategy.Id, strategy.Configuration.CooldownPeriod));

        var order = orderRepo.Added[0];
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(PositionSide.Long, order.PositionSide);

        // S66-A：確認下單時帶有決定性 ClientOrderId（而非隨機 Guid）
        Assert.NotNull(order.ClientOrderId);
        Assert.StartsWith("cb_", order.ClientOrderId!);
        Assert.Matches("^[a-z0-9_]+$", order.ClientOrderId);

        await executor.StopAsync();
    }

    [Fact]
    public async Task StrategyExecutor_SameKlineFiredTwice_YieldsSameClientOrderId()
    {
        // S66-A：驗證「同一根 K 線（close_time 相同）觸發兩次」產出的 ClientOrderId 完全一致。
        // 這才是網路 retry / SDK 重送 / process 重啟場景下的真正冪等性保證。
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

        // 關掉 cooldown 以便第二次 K 線不被擋
        var cfg = MakeConfig(cooldown: TimeSpan.Zero);
        var strategy = MakeStrategy(cfg);
        var strategyImpl = new FakeStrategy(signalFor: SignalType.OpenLong);

        await using var executor = new StrategyExecutor(
            strategy, strategyImpl, marketData, exchange, tracker,
            sp, new NoOpNotificationService(), new NullRealtimeBroadcaster(),
            new DeterministicClientOrderIdGenerator(),
            NullLogger<StrategyExecutor>.Instance);

        await executor.StartAsync();

        // 用同一個 closeTime 觸發兩次
        var fixedCloseTime = new DateTime(2026, 4, 25, 10, 30, 0, DateTimeKind.Utc);
        var kline = MakeKline(fixedCloseTime, 100m, 101m);

        await marketData.FireKlineUpdateAsync(cfg.Symbol, cfg.Interval, kline);
        await marketData.FireKlineUpdateAsync(cfg.Symbol, cfg.Interval, kline);

        // Assert — 兩筆訂單皆已產生（fake 不會去重），但 ClientOrderId 必須一致
        Assert.Equal(2, orderRepo.Added.Count);
        var cid1 = orderRepo.Added[0].ClientOrderId;
        var cid2 = orderRepo.Added[1].ClientOrderId;
        Assert.NotNull(cid1);
        Assert.Equal(cid1, cid2);

        await executor.StopAsync();
    }

    [Fact]
    public async Task StrategyExecutor_DifferentKlines_YieldDifferentClientOrderIds()
    {
        // S66-A：不同 K 線（close_time 不同）必須產出不同 ClientOrderId，
        // 否則同一策略在連續 K 線都發出訊號時會互相阻斷 — 那就不是冪等，是 bug。
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

        var cfg = MakeConfig(cooldown: TimeSpan.Zero);
        var strategy = MakeStrategy(cfg);
        var strategyImpl = new FakeStrategy(signalFor: SignalType.OpenLong);

        await using var executor = new StrategyExecutor(
            strategy, strategyImpl, marketData, exchange, tracker,
            sp, new NoOpNotificationService(), new NullRealtimeBroadcaster(),
            new DeterministicClientOrderIdGenerator(),
            NullLogger<StrategyExecutor>.Instance);

        await executor.StartAsync();

        var t1 = new DateTime(2026, 4, 25, 10, 30, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(15);

        await marketData.FireKlineUpdateAsync(cfg.Symbol, cfg.Interval, MakeKline(t1, 100m, 101m));
        await marketData.FireKlineUpdateAsync(cfg.Symbol, cfg.Interval, MakeKline(t2, 101m, 102m));

        Assert.Equal(2, orderRepo.Added.Count);
        Assert.NotEqual(orderRepo.Added[0].ClientOrderId, orderRepo.Added[1].ClientOrderId);

        await executor.StopAsync();
    }

    [Fact]
    public async Task StrategyExecutor_TraceId_PropagatesFromKlineToOrderAndBroadcast()
    {
        // S66-C：驗證 K 線 tick 生成的 TraceId 同時落在
        // (a) Order.TraceId（Domain entity）
        // (b) StrategyEvaluatedUpdate.TraceId（評估心跳廣播）
        // (c) TradeFilledUpdate.TraceId（成交廣播）
        // 三者必須是同一字串，且符合 12 字 hex 格式。
        var exchange = new FakeExchangeClient { Balance = 100_000m };
        exchange.PreloadKlines = MakeKlines(60);
        var marketData = new FakeMarketDataStream();
        var tracker = new StrategyCooldownTracker();
        var orderRepo = new FakeOrderRepository();
        var positionRepo = new FakePositionRepository();
        var unitOfWork = new FakeUnitOfWork();
        var capturingBroadcaster = new CapturingBroadcaster();

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
            sp, new NoOpNotificationService(), capturingBroadcaster,
            new DeterministicClientOrderIdGenerator(),
            NullLogger<StrategyExecutor>.Instance);

        await executor.StartAsync();

        await marketData.FireKlineUpdateAsync(
            strategy.Configuration.Symbol, strategy.Configuration.Interval,
            MakeKline(DateTime.UtcNow, 100m, 101m));

        // Assert
        Assert.Single(orderRepo.Added);
        var order = orderRepo.Added[0];
        Assert.NotNull(order.TraceId);
        Assert.Equal(12, order.TraceId!.Length);
        Assert.Matches("^[a-f0-9]+$", order.TraceId);

        Assert.Single(capturingBroadcaster.EvaluatedUpdates);
        var heartbeatTraceId = capturingBroadcaster.EvaluatedUpdates[0].TraceId;
        Assert.Equal(order.TraceId, heartbeatTraceId);

        Assert.Single(capturingBroadcaster.TradeUpdates);
        var tradeTraceId = capturingBroadcaster.TradeUpdates[0].TraceId;
        Assert.Equal(order.TraceId, tradeTraceId);

        await executor.StopAsync();
    }

    [Fact]
    public async Task StrategyExecutor_DifferentKlineTicks_GenerateDifferentTraceIds()
    {
        // S66-C：兩根不同 kline tick 必須產出不同的 TraceId（每筆訊號鏈路獨立）。
        var exchange = new FakeExchangeClient { Balance = 100_000m };
        exchange.PreloadKlines = MakeKlines(60);
        var marketData = new FakeMarketDataStream();
        var tracker = new StrategyCooldownTracker();
        var orderRepo = new FakeOrderRepository();
        var positionRepo = new FakePositionRepository();
        var unitOfWork = new FakeUnitOfWork();
        var capturingBroadcaster = new CapturingBroadcaster();

        var sp = new FakeServiceProvider()
            .Register<IOrderRepository>(orderRepo)
            .Register<IPositionRepository>(positionRepo)
            .Register<IStrategyRepository>(new FakeStrategyRepository())
            .Register<IUnitOfWork>(unitOfWork)
            .Register<IOrderSizer>(new OrderSizer(exchange))
            .Register<IRiskManager>(new RiskManager(exchange, positionRepo, tracker, RiskLimits.Moderate));

        var cfg = MakeConfig(cooldown: TimeSpan.Zero);  // 關掉 cooldown 才能連送兩筆
        var strategy = MakeStrategy(cfg);
        var strategyImpl = new FakeStrategy(signalFor: SignalType.OpenLong);

        await using var executor = new StrategyExecutor(
            strategy, strategyImpl, marketData, exchange, tracker,
            sp, new NoOpNotificationService(), capturingBroadcaster,
            new DeterministicClientOrderIdGenerator(),
            NullLogger<StrategyExecutor>.Instance);

        await executor.StartAsync();

        var t1 = new DateTime(2026, 4, 25, 10, 30, 0, DateTimeKind.Utc);
        await marketData.FireKlineUpdateAsync(cfg.Symbol, cfg.Interval, MakeKline(t1, 100m, 101m));
        await marketData.FireKlineUpdateAsync(cfg.Symbol, cfg.Interval, MakeKline(t1.AddMinutes(15), 101m, 102m));

        Assert.Equal(2, orderRepo.Added.Count);
        Assert.NotEqual(orderRepo.Added[0].TraceId, orderRepo.Added[1].TraceId);

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
            new DeterministicClientOrderIdGenerator(),
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
            new DeterministicClientOrderIdGenerator(),
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

internal sealed class CapturingBroadcaster : IRealtimeBroadcaster
{
    public List<TradeFilledUpdate> TradeUpdates { get; } = new();
    public List<StrategyEvaluatedUpdate> EvaluatedUpdates { get; } = new();
    public List<StrategyEvaluationFailedUpdate> EvaluationFailedUpdates { get; } = new();

    public Task BroadcastTradeAsync(TradeFilledUpdate update, CancellationToken ct = default)
    {
        TradeUpdates.Add(update);
        return Task.CompletedTask;
    }

    public Task BroadcastStrategyEvaluatedAsync(StrategyEvaluatedUpdate update, CancellationToken ct = default)
    {
        EvaluatedUpdates.Add(update);
        return Task.CompletedTask;
    }

    public Task BroadcastStrategyEvaluationFailedAsync(StrategyEvaluationFailedUpdate update, CancellationToken ct = default)
    {
        EvaluationFailedUpdates.Add(update);
        return Task.CompletedTask;
    }

    // 其他不關心的事件給 no-op
    public Task BroadcastStatsAsync(DashboardStatsUpdate update, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastPositionClosedAsync(PositionClosedUpdate update, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastPositionPnLAsync(PositionPnLTickUpdate update, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastStrategyMetadataChangedAsync(StrategyMetadataChangedUpdate update, CancellationToken ct = default) => Task.CompletedTask;
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
    public Task<IReadOnlyList<ExchangeOpenOrderInfo>> GetOpenOrdersAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeOpenOrderInfo>>(Array.Empty<ExchangeOpenOrderInfo>());

    public Task<ExchangeOrderSnapshot?> GetOrderByClientOrderIdAsync(
        Symbol symbol, string clientOrderId, CancellationToken ct = default) =>
        Task.FromResult<ExchangeOrderSnapshot?>(null);

    public Task<DateTime> GetServerTimeAsync(CancellationToken ct = default) =>
        Task.FromResult(DateTime.UtcNow);
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
    public Task<Order?> GetByClientOrderIdAsync(string clientOrderId, CancellationToken ct = default) => Task.FromResult<Order?>(Added.FirstOrDefault(o => o.ClientOrderId == clientOrderId));
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
    public Task<IReadOnlyList<Position>> GetRecentClosedAsync(int limit, CancellationToken ct = default) =>
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
    public Task<int> SaveChangesWithRetryAsync(int maxAttempts = 3, CancellationToken ct = default)
        => SaveChangesAsync(ct);
    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
}
