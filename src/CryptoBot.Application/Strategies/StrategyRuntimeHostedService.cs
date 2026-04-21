using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Synchronization;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Strategies;

/// <summary>
/// 機器人主執行緒。取代舊的 <c>CryptoBotHostedService</c>（純心跳服務）。
///
/// 啟動順序：
/// 1. <see cref="IMarketDataStream.StartAsync"/> — 準備好 user-data 與行情 WS
/// 2. <see cref="IAccountSynchronizer.StartAsync"/> — 掛 WS handler
/// 3. <see cref="IAccountSynchronizer.ReconcileAsync"/> — REST 對帳補齊離線期間漏接的事件
/// 4. 從 DB 拉出 <see cref="StrategyStatus.Running"/> 狀態的策略
/// 5. 為每個策略造 Executor → 啟動
///
/// 停機順序（反向）：
/// 1. 停掉所有 Executor（等候 in-flight tick 處理完）
/// 2. 停掉 Synchronizer（拆 WS handler）
/// 3. 停掉 MarketDataStream（關閉 WS / REST listenKey）
///
/// 這個 Service 也實作 <see cref="IStrategyRuntimeController"/>，給 Web API 做熱啟停使用。
/// Start/Stop/Load 都用單一 <see cref="SemaphoreSlim"/> 串行化，避免 API 併發調用時
/// 同一個策略被多次啟動或 _executors 集合被破壞。
/// </summary>
public sealed class StrategyRuntimeHostedService : IHostedService, IStrategyRuntimeController, IAsyncDisposable
{
    private readonly IMarketDataStream _marketData;
    private readonly IAccountSynchronizer _synchronizer;
    private readonly IStrategyExecutorFactory _executorFactory;
    private readonly IStrategyFactory _strategyFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StrategyRuntimeHostedService> _logger;

    private readonly Dictionary<Guid, IStrategyExecutor> _executors = new();
    private readonly SemaphoreSlim _mutateLock = new(1, 1);
    private bool _disposed;

    public StrategyRuntimeHostedService(
        IMarketDataStream marketData,
        IAccountSynchronizer synchronizer,
        IStrategyExecutorFactory executorFactory,
        IStrategyFactory strategyFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<StrategyRuntimeHostedService> logger)
    {
        _marketData = marketData;
        _synchronizer = synchronizer;
        _executorFactory = executorFactory;
        _strategyFactory = strategyFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>僅供測試 — 檢視目前掛載的 Executor 數量。</summary>
    internal int ExecutorCount
    {
        get { lock (_executors) return _executors.Count; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StrategyRuntimeHostedService starting…");

        await _marketData.StartAsync(cancellationToken).ConfigureAwait(false);
        await _synchronizer.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _synchronizer.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconciliation failed at startup — continuing with live events only.");
        }

        await LoadAndStartActiveStrategiesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "StrategyRuntimeHostedService started with {Count} active executors.",
            ExecutorCount);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _mutateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation(
                "StrategyRuntimeHostedService stopping {Count} executors…",
                _executors.Count);

            foreach (var exec in _executors.Values)
            {
                try
                {
                    await exec.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Error stopping executor {StrategyId} — continuing shutdown.", exec.StrategyId);
                }
            }
            _executors.Clear();
        }
        finally
        {
            _mutateLock.Release();
        }

        try { await _synchronizer.StopAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error stopping synchronizer."); }

        try { await _marketData.StopAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error stopping market data stream."); }

        _logger.LogInformation("StrategyRuntimeHostedService stopped.");
    }

    private async Task LoadAndStartActiveStrategiesAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();

        var running = await repo.GetByStatusAsync(StrategyStatus.Running, ct).ConfigureAwait(false);
        if (running.Count == 0)
        {
            _logger.LogInformation("No strategies in Running state — runtime idle (waiting for external activation).");
            return;
        }

        foreach (var strategy in running)
        {
            try
            {
                var impl = _strategyFactory.Get(strategy.StrategyType);
                var executor = _executorFactory.Create(strategy, impl);
                await executor.StartAsync(ct).ConfigureAwait(false);
                _executors[strategy.Id] = executor;

                _logger.LogInformation(
                    "Executor started for strategy {Name} ({Type}) on {Symbol}.",
                    strategy.Name, strategy.StrategyType, strategy.Configuration.Symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to start strategy {Name} ({Type}) — continuing with others.",
                    strategy.Name, strategy.StrategyType);
            }
        }
    }

    // ───────── IStrategyRuntimeController ─────────

    public bool IsRunning(Guid strategyId)
    {
        lock (_executors) return _executors.ContainsKey(strategyId);
    }

    public IReadOnlyList<Guid> RunningStrategyIds
    {
        get { lock (_executors) return _executors.Keys.ToArray(); }
    }

    public async Task<IReadOnlyList<Guid>> StopAllAsync(string reason, CancellationToken ct = default)
    {
        await _mutateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_executors.Count == 0) return Array.Empty<Guid>();

            var stopped = new List<Guid>(_executors.Count);

            // 先關 executor（停 tick 迴圈、讓 in-flight 動作 drain），再處理 DB 狀態
            foreach (var (id, executor) in _executors.ToArray())
            {
                try { await executor.StopAsync(ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Executor stop threw for {Id} during StopAll — continuing.", id);
                }
                stopped.Add(id);
            }
            _executors.Clear();

            // DB 狀態翻為 Stopped（帶原因）
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            foreach (var id in stopped)
            {
                try
                {
                    var strategy = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
                    if (strategy is null) continue;
                    strategy.Stop(reason);
                    await repo.UpdateAsync(strategy, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to flip DB status to Stopped for {Id} during StopAll — continuing.", id);
                }
            }
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogWarning("⏹ StopAll: {Count} strategies stopped. Reason: {Reason}",
                stopped.Count, reason);

            return stopped;
        }
        finally
        {
            _mutateLock.Release();
        }
    }

    public async Task<bool> StartAsync(Guid strategyId, CancellationToken ct = default)
    {
        await _mutateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_executors.ContainsKey(strategyId))
            {
                _logger.LogInformation("Strategy {Id} already running — Start() is a no-op.", strategyId);
                return true;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var strategy = await repo.GetByIdAsync(strategyId, ct).ConfigureAwait(false);
            if (strategy is null)
            {
                _logger.LogWarning("Start requested for unknown strategy {Id}.", strategyId);
                return false;
            }

            // Aggregate 層的狀態 + DB 一起更新（如果 Start() 因為狀態不對拋例外，會被外層 catch）
            if (strategy.Status != StrategyStatus.Running)
                strategy.Start();
            await repo.UpdateAsync(strategy, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            var impl = _strategyFactory.Get(strategy.StrategyType);
            var executor = _executorFactory.Create(strategy, impl);
            await executor.StartAsync(ct).ConfigureAwait(false);
            _executors[strategy.Id] = executor;

            _logger.LogInformation("▶ Strategy {Name} ({Id}) started via API.", strategy.Name, strategy.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start strategy {Id} via API.", strategyId);
            return false;
        }
        finally
        {
            _mutateLock.Release();
        }
    }

    public async Task<bool> StopAsync(Guid strategyId, CancellationToken ct = default)
    {
        await _mutateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_executors.TryGetValue(strategyId, out var executor))
            {
                _logger.LogInformation("Stop requested for non-running strategy {Id} — checking DB to flip status anyway.", strategyId);
            }
            else
            {
                try { await executor.StopAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Executor stop threw for {Id} — removing from table anyway.", strategyId); }
                _executors.Remove(strategyId);
            }

            // DB 狀態同步翻為 Stopped（冪等）
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var strategy = await repo.GetByIdAsync(strategyId, ct).ConfigureAwait(false);
            if (strategy is null) return false;

            strategy.Stop("Stopped via API");
            await repo.UpdateAsync(strategy, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("⏹ Strategy {Name} ({Id}) stopped via API.", strategy.Name, strategy.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop strategy {Id} via API.", strategyId);
            return false;
        }
        finally
        {
            _mutateLock.Release();
        }
    }

    public async Task<bool> ChangeStrategyTypeAsync(Guid strategyId, string newStrategyType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newStrategyType))
        {
            _logger.LogWarning("ChangeStrategyType rejected for {Id}: empty type.", strategyId);
            return false;
        }

        // 類型必須先存在於 IStrategyFactory；找不到就直接拒絕 — 不動 DB、不動 executor。
        try
        {
            _ = _strategyFactory.Get(newStrategyType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ChangeStrategyType rejected for {Id}: unknown type '{Type}'.",
                strategyId, newStrategyType);
            return false;
        }

        await _mutateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var strategy = await repo.GetByIdAsync(strategyId, ct).ConfigureAwait(false);
            if (strategy is null)
            {
                _logger.LogWarning("ChangeStrategyType: unknown strategy {Id}.", strategyId);
                return false;
            }

            // 1) 如果在跑 → 先停 executor（避免熱換期間還在跑舊腦 tick）
            var wasRunning = _executors.TryGetValue(strategyId, out var runningExecutor);
            if (wasRunning && runningExecutor is not null)
            {
                try { await runningExecutor.StopAsync(ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Executor stop threw during ChangeStrategyType for {Id} — removing anyway.",
                        strategyId);
                }
                _executors.Remove(strategyId);
            }

            // 2) 翻 DB：先把 Status 退回 Stopped（否則 ChangeType 會拒絕），再換 Type
            var originalStatus = strategy.Status;
            if (strategy.Status == StrategyStatus.Running)
                strategy.Stop("Type change in progress");

            try
            {
                strategy.ChangeType(newStrategyType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Domain refused ChangeType for {Id} to '{Type}'.", strategyId, newStrategyType);
                return false;
            }

            // 3) 如果之前在跑，立刻把 Status 拉回 Running 並重建 executor
            if (wasRunning || originalStatus == StrategyStatus.Running)
                strategy.Start();

            await repo.UpdateAsync(strategy, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            if (wasRunning || originalStatus == StrategyStatus.Running)
            {
                var impl = _strategyFactory.Get(newStrategyType);
                var executor = _executorFactory.Create(strategy, impl);
                await executor.StartAsync(ct).ConfigureAwait(false);
                _executors[strategy.Id] = executor;

                _logger.LogInformation(
                    "⇆ Strategy {Name} ({Id}) type hot-swapped to {Type} (resumed Running).",
                    strategy.Name, strategy.Id, newStrategyType);
            }
            else
            {
                _logger.LogInformation(
                    "⇆ Strategy {Name} ({Id}) type changed to {Type} (stays Stopped).",
                    strategy.Name, strategy.Id, newStrategyType);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeStrategyType failed for {Id}.", strategyId);
            return false;
        }
        finally
        {
            _mutateLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var exec in _executors.Values)
        {
            try { await exec.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow during dispose */ }
        }
        _executors.Clear();
        _mutateLock.Dispose();
    }
}
