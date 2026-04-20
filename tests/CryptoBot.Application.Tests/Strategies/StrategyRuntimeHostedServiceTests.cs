using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Strategies;
using CryptoBot.Application.Synchronization;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies;

public class StrategyRuntimeHostedServiceTests
{
    private static StrategyConfiguration MakeConfig() =>
        StrategyConfiguration.Create(
            Symbol.Parse("BTC-USDT"),
            KlineInterval.FifteenMinutes,
            Leverage.Moderate,
            0.02m, 0.02m, 0.04m);

    private static Strategy MakeRunningStrategy(string type = "Fake")
    {
        var s = Strategy.Create("Test", type, MakeConfig());
        s.Start();  // 變 Running
        return s;
    }

    private static (StrategyRuntimeHostedService svc,
                    RuntimeFakeMarketDataStream market,
                    FakeSync synchronizer,
                    RecordingExecutorFactory executorFactory,
                    RecordingStrategyRepo strategyRepo)
        Build(params Strategy[] runningStrategies)
    {
        var market = new RuntimeFakeMarketDataStream();
        var sync = new FakeSync();
        var executorFactory = new RecordingExecutorFactory();
        var strategyRepo = new RecordingStrategyRepo();
        foreach (var s in runningStrategies) strategyRepo.Seed(s);

        var strategyImpl = new RuntimeFakeStrategy("Fake");
        var strategyFactory = new StrategyFactory(new IStrategy[] { strategyImpl });

        var sp = new RuntimeFakeScopeFactory()
            .Register<IStrategyRepository>(strategyRepo);

        var svc = new StrategyRuntimeHostedService(
            market, sync, executorFactory, strategyFactory, sp,
            NullLogger<StrategyRuntimeHostedService>.Instance);

        return (svc, market, sync, executorFactory, strategyRepo);
    }

    [Fact]
    public async Task StartAsync_NoRunningStrategies_StartsStreamsAndZeroExecutors()
    {
        var (svc, market, sync, factory, _) = Build();

        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(1, market.StartCalls);
        Assert.Equal(1, sync.StartCalls);
        Assert.Equal(1, sync.ReconcileCalls);
        Assert.Empty(factory.Created);
        Assert.Equal(0, svc.ExecutorCount);
    }

    [Fact]
    public async Task StartAsync_WithRunningStrategies_CreatesAndStartsExecutors()
    {
        var s1 = MakeRunningStrategy();
        var s2 = MakeRunningStrategy();
        var (svc, _, _, factory, _) = Build(s1, s2);

        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(2, factory.Created.Count);
        Assert.All(factory.Created, e => Assert.Equal(1, e.StartCalls));
        Assert.Equal(2, svc.ExecutorCount);
    }

    [Fact]
    public async Task StopAsync_StopsExecutorsThenSynchronizerThenMarket()
    {
        var s = MakeRunningStrategy();
        var (svc, market, sync, factory, _) = Build(s);

        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        Assert.All(factory.Created, e => Assert.Equal(1, e.StopCalls));
        Assert.Equal(1, sync.StopCalls);
        Assert.Equal(1, market.StopCalls);
    }

    [Fact]
    public async Task StartAsync_ReconcileFails_StillLoadsStrategies()
    {
        var s = MakeRunningStrategy();
        var (svc, _, sync, factory, _) = Build(s);
        sync.ReconcileThrows = true;

        // Should not throw
        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(1, sync.ReconcileCalls);
        Assert.Single(factory.Created);
        Assert.Equal(1, svc.ExecutorCount);
    }

    [Fact]
    public async Task StartAsync_IndividualExecutorFailure_ContinuesWithOthers()
    {
        var s1 = MakeRunningStrategy();
        var s2 = MakeRunningStrategy();
        var (svc, _, _, factory, _) = Build(s1, s2);
        factory.FailFirst = true;

        await svc.StartAsync(CancellationToken.None);

        // factory.Created records both; one failed to start → only the second lands in _executors
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(1, svc.ExecutorCount);
    }

    [Fact]
    public async Task StartAsync_OnlyLoadsRunningStatus()
    {
        var running = MakeRunningStrategy();

        var stopped = Strategy.Create("Stopped", "Fake", MakeConfig());
        stopped.Start();
        stopped.Stop("manual");

        var (svc, _, _, factory, repo) = Build();
        repo.Seed(running);
        repo.Seed(stopped);

        await svc.StartAsync(CancellationToken.None);

        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task StopAsync_SwallowsExecutorStopErrors()
    {
        var s = MakeRunningStrategy();
        var (svc, market, sync, factory, _) = Build(s);
        factory.StopThrows = true;

        await svc.StartAsync(CancellationToken.None);
        var ex = await Record.ExceptionAsync(() => svc.StopAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.Equal(1, sync.StopCalls);
        Assert.Equal(1, market.StopCalls);
    }
}

// ═════════════════════════════════════════════════════════
// Fakes
// ═════════════════════════════════════════════════════════

internal sealed class RuntimeFakeScopeFactory : IServiceProvider, IServiceScope, IServiceScopeFactory
{
    private readonly Dictionary<Type, object> _map = new();

    public RuntimeFakeScopeFactory Register<T>(T instance) where T : class
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

internal sealed class RuntimeFakeMarketDataStream : IMarketDataStream
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

    private void TouchUnused()
    {
        OnKlineUpdate?.Invoke(default!, default!, default!);
        OnPriceUpdate?.Invoke(default!, default!);
        OnExchangeOrderUpdate?.Invoke(default!);
        OnExchangeAccountUpdate?.Invoke(default!);
    }
}

internal sealed class FakeSync : IAccountSynchronizer
{
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int ReconcileCalls { get; private set; }
    public bool ReconcileThrows { get; set; }

    public Task StartAsync(CancellationToken ct = default) { StartCalls++; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default) { StopCalls++; return Task.CompletedTask; }
    public Task ReconcileAsync(CancellationToken ct = default)
    {
        ReconcileCalls++;
        if (ReconcileThrows) throw new InvalidOperationException("reconcile boom");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingExecutor : IStrategyExecutor
{
    public Guid StrategyId { get; }
    public bool IsRunning { get; private set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool ThrowOnStart { get; set; }
    public bool ThrowOnStop { get; set; }

    public RecordingExecutor(Guid strategyId) => StrategyId = strategyId;

    public Task StartAsync(CancellationToken ct = default)
    {
        StartCalls++;
        if (ThrowOnStart) throw new InvalidOperationException("start boom");
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        StopCalls++;
        if (ThrowOnStop) throw new InvalidOperationException("stop boom");
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
}

internal sealed class RecordingExecutorFactory : IStrategyExecutorFactory
{
    public List<RecordingExecutor> Created { get; } = new();
    public bool FailFirst { get; set; }
    public bool StopThrows { get; set; }

    public IStrategyExecutor Create(Strategy strategy, IStrategy strategyImpl)
    {
        var exec = new RecordingExecutor(strategy.Id);
        if (FailFirst && Created.Count == 0) exec.ThrowOnStart = true;
        if (StopThrows) exec.ThrowOnStop = true;
        Created.Add(exec);
        return exec;
    }
}

internal sealed class RuntimeFakeStrategy : IStrategy
{
    public RuntimeFakeStrategy(string type) => StrategyType = type;
    public string StrategyType { get; }
    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<CryptoBot.Domain.Aggregates.PositionAggregate.Position> openPositions,
        CancellationToken ct = default) =>
        Task.FromResult(TradingSignal.None(config.Symbol, Price.Create(100m)));
}

internal sealed class RecordingStrategyRepo : IStrategyRepository
{
    private readonly List<Strategy> _store = new();
    public void Seed(Strategy s) => _store.Add(s);

    public Task<Strategy?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<Strategy?>(_store.FirstOrDefault(s => s.Id == id));

    public Task<Strategy?> GetByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<Strategy?>(_store.FirstOrDefault(s => s.Name == name));

    public Task<IReadOnlyList<Strategy>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Strategy>>(_store.ToList());

    public Task<IReadOnlyList<Strategy>> GetByStatusAsync(StrategyStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Strategy>>(_store.Where(s => s.Status == status).ToList());

    public Task AddAsync(Strategy strategy, CancellationToken ct = default) { _store.Add(strategy); return Task.CompletedTask; }
    public Task UpdateAsync(Strategy strategy, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
}
