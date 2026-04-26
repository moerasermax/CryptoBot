using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.RiskManagement;

/// <summary>
/// S28 T1：每分鐘巡檢 <see cref="IRiskManager.IsDailyLossLimitReachedAsync"/>，
/// 觸發時呼叫 <see cref="IStrategyRuntimeController.StopAllAsync"/> 停掉所有策略，
/// 並透過 <see cref="INotificationService"/> 發送 Critical 等級熔斷警報。
///
/// 為什麼獨立成 BackgroundService：
/// - <see cref="StrategyRuntimeHostedService"/> 的單一責任是策略生命週期；把 risk 巡檢塞進去
///   會讓該 host 同時扛兩件互不相關的事，單元測試變得難寫。
/// - 獨立後，monitor 可以自由透過 <see cref="IServiceScopeFactory"/> 建立短生命週期 scope
///   取得 Scoped 的 <see cref="IRiskManager"/>，不干擾 hosted service 的 scope 策略。
///
/// 跨日自動解除：每次 tick 若 <see cref="ISafetyBreakerState.TrippedAtUtc"/> 的日期早於今天，
/// 主動呼叫 <see cref="ISafetyBreakerState.Reset"/>(auto-reset) — 事件推播讓 UI 即時解鎖。
/// </summary>
public sealed class SafetyBreakerMonitor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStrategyRuntimeController _runtime;
    private readonly ISafetyBreakerState _breaker;
    private readonly INotificationService _notifications;
    private readonly ILogger<SafetyBreakerMonitor> _logger;

    public SafetyBreakerMonitor(
        IServiceScopeFactory scopeFactory,
        IStrategyRuntimeController runtime,
        ISafetyBreakerState breaker,
        INotificationService notifications,
        ILogger<SafetyBreakerMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime;
        _breaker = breaker;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SafetyBreakerMonitor started (poll interval {Interval}).", PollInterval);
        using var timer = new PeriodicTimer(PollInterval);

        // 啟動立即做一次，不用等第一個 tick 才檢查（避免剛重啟時熔斷狀態已在 DB / 倉位中但 UI 看不到）
        await SafeTickAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await SafeTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停機正常路徑
        }
        _logger.LogInformation("SafetyBreakerMonitor stopped.");
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try
        {
            await TickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 監控迴圈本身絕不可因為一次失敗就死掉 — 吞掉並等下一次 tick
            _logger.LogWarning(ex, "SafetyBreakerMonitor tick failed — will retry next interval.");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // 1) 跨日自動解除
        var trippedAt = _breaker.TrippedAtUtc;
        if (trippedAt is not null && trippedAt.Value.Date < DateTime.UtcNow.Date)
        {
            if (_breaker.Reset("auto-reset (UTC day rollover)"))
                _logger.LogInformation("Safety breaker auto-reset at UTC day rollover.");
        }

        // 2) 已在熔斷中：不再重複觸發動作；讓 Reset 路徑負責解鎖
        if (_breaker.IsTripped) return;

        // 3) 巡檢：用 scope 抓 Scoped 的 IRiskManager
        bool limitReached;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var risk = scope.ServiceProvider.GetRequiredService<IRiskManager>();
            limitReached = await risk.IsDailyLossLimitReachedAsync(ct).ConfigureAwait(false);
        }

        if (!limitReached) return;

        // 4) 觸發熔斷 — 先切狀態再停策略，確保 API 端的 Start 從此被拒
        const string reason = "Daily loss limit reached";
        if (!_breaker.Trip(reason))
            return; // 其他執行緒搶先觸發，這次無事可做

        _logger.LogCritical("🛑 CIRCUIT BREAKER TRIPPED — {Reason}. Stopping all strategies.", reason);

        try
        {
            await _runtime.StopAllAsync(reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StopAllAsync threw during breaker trip — strategies may still be running.");
        }

        try
        {
            await _notifications.NotifyCircuitBreakerAsync(reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Circuit breaker notification dispatch failed.");
        }
    }
}
