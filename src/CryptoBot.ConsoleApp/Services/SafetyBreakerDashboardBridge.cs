using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.ConsoleApp.Realtime;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// S28 T1：把 <see cref="ISafetyBreakerState"/> 的 Tripped / ResetBySupervisor 事件轉發到
/// <see cref="DashboardEventBus"/>，讓 Blazor UI 能即時鎖住 / 解鎖控制台。
///
/// 這裡獨立成 BackgroundService 的理由：
/// - <see cref="ISafetyBreakerState"/> 住在 Application 層（不認識 Console 的 Bus）；
/// - <see cref="DashboardEventBus"/> 住在 ConsoleApp 層。
/// - 必須在 ConsoleApp 組裝的這一邊做橋接 — 同時也讓單元測試不用背這條耦合。
///
/// 生命週期：<see cref="StartAsync"/> 時掛 handler、<see cref="StopAsync"/> 時解掛，確保 host
/// 關閉時不留事件訂閱（避免記憶體洩漏或在 Bus 已釋放後被 Invoke）。
/// </summary>
public sealed class SafetyBreakerDashboardBridge : IHostedService
{
    private readonly ISafetyBreakerState _breaker;
    private readonly DashboardEventBus _bus;
    private readonly ILogger<SafetyBreakerDashboardBridge> _logger;

    private Action<string>? _tripHandler;
    private Action<string>? _resetHandler;

    public SafetyBreakerDashboardBridge(
        ISafetyBreakerState breaker,
        DashboardEventBus bus,
        ILogger<SafetyBreakerDashboardBridge> logger)
    {
        _breaker = breaker;
        _bus = bus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _tripHandler = reason =>
        {
            _logger.LogWarning("🛑 Breaker tripped: {Reason}", reason);
            _bus.RaiseBreakerState(new BreakerStateUpdate(
                IsTripped: true,
                TrippedAtUtc: _breaker.TrippedAtUtc,
                Reason: reason));
        };
        _resetHandler = note =>
        {
            _logger.LogInformation("✅ Breaker reset: {Note}", note);
            _bus.RaiseBreakerState(new BreakerStateUpdate(
                IsTripped: false,
                TrippedAtUtc: null,
                Reason: null));
        };

        _breaker.Tripped += _tripHandler;
        _breaker.ResetBySupervisor += _resetHandler;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_tripHandler is not null) _breaker.Tripped -= _tripHandler;
        if (_resetHandler is not null) _breaker.ResetBySupervisor -= _resetHandler;
        return Task.CompletedTask;
    }
}
