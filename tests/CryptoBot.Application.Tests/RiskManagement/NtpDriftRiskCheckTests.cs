using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.RiskManagement;

/// <summary>
/// S66-D：RiskManager 對時鐘漂移的攔截行為。
/// 偏差超過 1000ms 時必須拒絕下單；尚未同步過時不擋（避免啟動窗口誤殺）。
/// </summary>
public class NtpDriftRiskCheckTests
{
    [Fact]
    public async Task SkewExceedsThreshold_RejectsOrder()
    {
        var skewState = new ClockSkewState();
        skewState.Update(TimeSpan.FromMilliseconds(1500), DateTime.UtcNow);

        var risk = BuildRiskManager(skewState);
        var (strategy, signal) = MakeStrategyAndSignal();

        var result = await risk.CheckBeforeOpenAsync(strategy, signal, Quantity.Create(1m));

        Assert.False(result.IsApproved);
        Assert.Contains("NTP Drift detected", result.Reason);
        Assert.Contains("1500ms", result.Reason);
    }

    [Fact]
    public async Task NegativeSkewExceedsThreshold_AlsoRejects()
    {
        // 伺服器落後本地 -1500ms 也應該擋（絕對值判斷）
        var skewState = new ClockSkewState();
        skewState.Update(TimeSpan.FromMilliseconds(-1500), DateTime.UtcNow);

        var risk = BuildRiskManager(skewState);
        var (strategy, signal) = MakeStrategyAndSignal();

        var result = await risk.CheckBeforeOpenAsync(strategy, signal, Quantity.Create(1m));

        Assert.False(result.IsApproved);
        Assert.Contains("NTP Drift", result.Reason);
        Assert.Contains("-1500", result.Reason);
    }

    [Fact]
    public async Task SkewWithinThreshold_DoesNotRejectByNtp()
    {
        // 800ms 在 warning 區間但仍 < 1000，不該被 NTP 條款攔
        var skewState = new ClockSkewState();
        skewState.Update(TimeSpan.FromMilliseconds(800), DateTime.UtcNow);

        var risk = BuildRiskManager(skewState);
        var (strategy, signal) = MakeStrategyAndSignal();

        var result = await risk.CheckBeforeOpenAsync(strategy, signal, Quantity.Create(1m));

        // 不該因 NTP 拒絕；其他檢查可能拒（或通過）— 只驗證 reason 不是 NTP 那條
        if (!result.IsApproved)
            Assert.DoesNotContain("NTP Drift", result.Reason ?? string.Empty);
    }

    [Fact]
    public async Task NotYetSynced_SkipsNtpCheckEntirely()
    {
        // ClockSkewState 從未被 Update → IsSynced=false → NTP 檢查跳過，
        // 即便 CurrentOffset 看起來是 0（初始值）也不該影響流程
        var skewState = new ClockSkewState();
        Assert.False(skewState.IsSynced);

        var risk = BuildRiskManager(skewState);
        var (strategy, signal) = MakeStrategyAndSignal();

        var result = await risk.CheckBeforeOpenAsync(strategy, signal, Quantity.Create(1m));

        // 不該因 NTP 拒絕（會走後續 cooldown / 倉位檢查）
        if (!result.IsApproved)
            Assert.DoesNotContain("NTP Drift", result.Reason ?? string.Empty);
    }

    [Fact]
    public async Task NullSkewState_BackwardCompatibility()
    {
        // 沒注入 ClockSkewState 時（舊測試 / 舊呼叫端），RiskManager 不該爆炸
        var risk = BuildRiskManager(skewState: null);
        var (strategy, signal) = MakeStrategyAndSignal();

        // 不該拋例外
        var result = await risk.CheckBeforeOpenAsync(strategy, signal, Quantity.Create(1m));

        if (!result.IsApproved)
            Assert.DoesNotContain("NTP Drift", result.Reason ?? string.Empty);
    }

    [Fact]
    public void ClockSkewState_InitialState_IsNotSynced()
    {
        var state = new ClockSkewState();

        Assert.False(state.IsSynced);
        Assert.Null(state.LastSyncedAtUtc);
        Assert.Equal(TimeSpan.Zero, state.CurrentOffset);
    }

    [Fact]
    public void ClockSkewState_AfterUpdate_ReflectsLatest()
    {
        var state = new ClockSkewState();
        var t1 = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(5);

        state.Update(TimeSpan.FromMilliseconds(250), t1);
        Assert.True(state.IsSynced);
        Assert.Equal(TimeSpan.FromMilliseconds(250), state.CurrentOffset);
        Assert.Equal(t1, state.LastSyncedAtUtc);

        state.Update(TimeSpan.FromMilliseconds(-100), t2);
        Assert.Equal(TimeSpan.FromMilliseconds(-100), state.CurrentOffset);
        Assert.Equal(t2, state.LastSyncedAtUtc);
    }

    [Fact]
    public void ClockSkewState_ConcurrentUpdates_DoesNotCorrupt()
    {
        // 雖然單行 Update 已用 lock 保護，多執行緒同時打也不該爆例外或撕裂值
        var state = new ClockSkewState();
        var baseline = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);

        Parallel.For(0, 1000, i =>
        {
            state.Update(TimeSpan.FromMilliseconds(i), baseline.AddSeconds(i));
        });

        // 收尾必有合法狀態（IsSynced=true、最後寫入的 LastSyncedAtUtc 對應 0..999 其中之一）
        Assert.True(state.IsSynced);
        Assert.NotNull(state.LastSyncedAtUtc);
    }

    // ============== Helpers ==============

    private static IRiskManager BuildRiskManager(IClockSkewState? skewState)
    {
        return new RiskManager(
            exchange: new SkewTestFakeExchange(),
            positionRepository: new SkewTestFakePositionRepo(),
            cooldownTracker: new StrategyCooldownTracker(),
            limits: RiskLimits.Moderate,
            skewState: skewState);
    }

    private static (Strategy strategy, TradingSignal signal) MakeStrategyAndSignal()
    {
        var cfg = StrategyConfiguration.Create(
            symbol: Symbol.Parse("BTC-USDT"),
            interval: KlineInterval.FifteenMinutes,
            leverage: Leverage.Moderate,
            riskPerTradePercent: 0.02m,
            stopLossPercent: 0.02m,
            takeProfitPercent: 0.04m);
        var strategy = Strategy.Create("TestStrat", "TestType", cfg);
        strategy.Start();

        var signal = TradingSignal.OpenLong(
            symbol: Symbol.Parse("BTC-USDT"),
            entry: Price.Create(50000m),
            stopLoss: null,
            takeProfit: null,
            confidence: 1.0m,
            reason: "test signal");

        return (strategy, signal);
    }
}

// 局部最小 fake — 只滿足 RiskManager 的依賴，本測試焦點在 NTP 攔截分支
internal sealed class SkewTestFakeExchange : IExchangeClient
{
    public string ExchangeName => "Fake";
    public string QuoteAsset => "VST";
    public TradingMode CurrentMode => TradingMode.Demo;
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;
    public Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default) => Task.FromResult(100_000m);
    public Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default) => Task.FromResult(0m);
    public Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Kline>> GetKlinesAsync(Symbol symbol, KlineInterval interval, int limit = 500, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Kline>>(Array.Empty<Kline>());
    public Task<Price> GetMarkPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(Price.Create(50000m));
    public Task<Price> GetSpotPriceAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(Price.Create(50000m));
    public Task<MarketSnapshot> GetMarketSnapshotAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(MarketSnapshot.Create(symbol, DateTime.UtcNow,
            Price.Create(50000m), Price.Create(50000m), Price.Create(50000m)));
    public Task<SymbolTradingRules> GetTradingRulesAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult(new SymbolTradingRules(symbol, 0.0001m, decimal.MaxValue, 0.0001m, 0.1m, 5m, 125));
    public Task<DateTime> GetServerTimeAsync(CancellationToken ct = default) => Task.FromResult(DateTime.UtcNow);
    public Task PlaceOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelOrderAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ExchangeOrderSnapshot?> GetOrderByClientOrderIdAsync(Symbol symbol, string clientOrderId, CancellationToken ct = default) => Task.FromResult<ExchangeOrderSnapshot?>(null);
    public Task<IReadOnlyList<ExchangeOpenOrderInfo>> GetOpenOrdersAsync(Symbol symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeOpenOrderInfo>>(Array.Empty<ExchangeOpenOrderInfo>());
    public Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangePositionInfo>>(Array.Empty<ExchangePositionInfo>());
    public Task<IReadOnlyList<ExchangeTradeInfo>> GetTradeHistoryAsync(Symbol symbol, DateTime since, DateTime? until = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeTradeInfo>>(Array.Empty<ExchangeTradeInfo>());
}

internal sealed class SkewTestFakePositionRepo : IPositionRepository
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
