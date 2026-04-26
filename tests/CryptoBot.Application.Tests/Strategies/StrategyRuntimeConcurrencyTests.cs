using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies;

/// <summary>
/// S27 T3 [VCP-Concurrency]：驗證 <see cref="StrategyRuntimeHostedService"/> 的
/// <c>_mutateLock</c> 在多策略同時 start / stop 時能正確序列化——不漏裝、不死鎖、
/// 不留孤兒 executor。
///
/// <para>
/// 為什麼這個測試需要：API 層的熱啟停允許使用者在 UI 連點多個策略的 Start/Stop，
/// 早期版本（pre-S27）沒有 SemaphoreSlim，會出現 <c>_executors</c> dictionary 被
/// 兩條 thread 同時寫導致例外、或 DB 狀態翻到一半就被下一個呼叫覆蓋。
/// </para>
///
/// <para>
/// Fake executor 在 Start / Stop 裡塞 <c>Task.Delay(20)</c> 刻意拉長臨界區，放大
/// race window — 若 lock 失效，<c>Dictionary.Add</c> 會拋 <c>InvalidOperationException</c>
/// （"Collection was modified"）或 Count 跑飛。
/// </para>
/// </summary>
public class StrategyRuntimeConcurrencyTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static StrategyConfiguration MakeConfig() =>
        StrategyConfiguration.Create(
            Symbol.Parse("BTC-USDT"),
            KlineInterval.FifteenMinutes,
            Leverage.Moderate,
            0.02m, 0.02m, 0.04m);

    private static Strategy MakeStoppedStrategy(int index)
    {
        // 策略需要先存在於 repo；本測試以 Stopped 起始然後 StartAsync — 不讓 LoadAndStart
        // 在 Host.StartAsync 內自動啟動，避免與外部 ParallelStart 打架。
        return Strategy.Create($"Concurrent-{index}", "Fake", MakeConfig());
    }

    private static StrategyRuntimeHostedService BuildService(
        Strategy[] strategies,
        RecordingExecutorFactory factory,
        RecordingStrategyRepo repo)
    {
        foreach (var s in strategies) repo.Seed(s);

        var strategyImpl = new RuntimeFakeStrategy("Fake");
        var strategyFactory = new StrategyFactory(new IStrategy[] { strategyImpl });

        var sp = new ConcurrencyFakeScopeFactory()
            .Register<IStrategyRepository>(repo)
            .Register<IUnitOfWork>(new NoopUnitOfWork());

        return new StrategyRuntimeHostedService(
            new RuntimeFakeMarketDataStream(),
            new FakeSync(),
            factory,
            strategyFactory,
            sp,
            new SafetyBreakerState(),
            NullLogger<StrategyRuntimeHostedService>.Instance);
    }

    [Fact]
    public async Task ParallelStart_FiveStrategies_AllEndUpRunning_NoDeadlock()
    {
        var strategies = Enumerable.Range(1, 5).Select(MakeStoppedStrategy).ToArray();
        var factory = new RecordingExecutorFactory { StartDelayMs = 20 };
        var repo = new RecordingStrategyRepo();
        await using var svc = BuildService(strategies, factory, repo);

        using var cts = new CancellationTokenSource(TestTimeout);

        // 5 策略同時 Start — 若沒鎖 _executors 會被併發寫導致例外或漏裝
        var startTasks = strategies.Select(s => svc.StartAsync(s.Id, cts.Token)).ToArray();
        var startResults = await Task.WhenAll(startTasks);

        Assert.All(startResults, ok => Assert.True(ok, "每個 Start 都應回 true"));
        Assert.Equal(5, svc.ExecutorCount);
        Assert.Equal(5, svc.RunningStrategyIds.Count);

        // 每個 executor 都恰好被 Start 一次
        Assert.Equal(5, factory.Created.Count);
        Assert.All(factory.Created, e => Assert.Equal(1, e.StartCalls));
    }

    [Fact]
    public async Task ParallelStop_FiveStrategies_AllStopped_NoOrphan()
    {
        var strategies = Enumerable.Range(1, 5).Select(MakeStoppedStrategy).ToArray();
        var factory = new RecordingExecutorFactory { StartDelayMs = 5, StopDelayMs = 20 };
        var repo = new RecordingStrategyRepo();
        await using var svc = BuildService(strategies, factory, repo);

        using var cts = new CancellationTokenSource(TestTimeout);

        // 先序列啟動以避免 Start 階段本身干擾 Stop 測試
        foreach (var s in strategies)
            await svc.StartAsync(s.Id, cts.Token);
        Assert.Equal(5, svc.ExecutorCount);

        // 5 條 Stop 並發
        var stopTasks = strategies.Select(s => svc.StopAsync(s.Id, cts.Token)).ToArray();
        var stopResults = await Task.WhenAll(stopTasks);

        Assert.All(stopResults, ok => Assert.True(ok));
        Assert.Equal(0, svc.ExecutorCount);
        Assert.Empty(svc.RunningStrategyIds);

        Assert.All(factory.Created, e => Assert.Equal(1, e.StopCalls));
    }

    [Fact]
    public async Task ParallelStartAndStop_MixedOps_ConvergesWithoutDeadlockOrException()
    {
        // 同時觸發 Start 與 Stop — 終態視 lock 的串行化而定，但必須「不例外、不超時」。
        var strategies = Enumerable.Range(1, 5).Select(MakeStoppedStrategy).ToArray();
        var factory = new RecordingExecutorFactory { StartDelayMs = 10, StopDelayMs = 10 };
        var repo = new RecordingStrategyRepo();
        await using var svc = BuildService(strategies, factory, repo);

        using var cts = new CancellationTokenSource(TestTimeout);

        var ops = new List<Task>();
        foreach (var s in strategies)
        {
            ops.Add(svc.StartAsync(s.Id, cts.Token));
            ops.Add(svc.StopAsync(s.Id, cts.Token));  // 可能比 Start 早跑完 — 是冪等且合法的
        }

        // 沒 deadlock 就會在 TestTimeout 前全部完成
        var completed = Task.WhenAll(ops);
        var delay = Task.Delay(TestTimeout, CancellationToken.None);
        var winner = await Task.WhenAny(completed, delay);
        Assert.Same(completed, winner); // 任何 deadlock 都會讓 delay 先贏

        Assert.Null(completed.Exception); // 無任何例外（Task.WhenAll 的聚合例外）

        // 終態掃尾：把全部 Stop 一次，驗證 svc 仍在可操作狀態
        foreach (var s in strategies)
            await svc.StopAsync(s.Id, CancellationToken.None);
        Assert.Equal(0, svc.ExecutorCount);
    }
}

// ═════════════════════════════════════════════════════════
// Concurrency-specific fakes（避免改動 StrategyRuntimeHostedServiceTests.cs 裡既有的共用 fake）
// ═════════════════════════════════════════════════════════

internal sealed class ConcurrencyFakeScopeFactory : IServiceProvider, IServiceScope, IServiceScopeFactory
{
    private readonly Dictionary<Type, object> _map = new();

    public ConcurrencyFakeScopeFactory Register<T>(T instance) where T : class
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

internal sealed class NoopUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> SaveChangesWithRetryAsync(int maxAttempts = 3, CancellationToken ct = default)
        => Task.FromResult(0);
    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
}
