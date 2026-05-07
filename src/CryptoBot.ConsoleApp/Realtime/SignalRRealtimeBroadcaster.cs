using CryptoBot.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace CryptoBot.ConsoleApp.Realtime;

/// <summary>
/// Web host 下的 <see cref="IRealtimeBroadcaster"/> 實作 — 同時 fan-out 兩條通路：
/// <list type="bullet">
///   <item><see cref="IHubContext{THub}"/>：廣播給所有外部 SignalR 連線（Postman、手機 App 等）。</item>
///   <item><see cref="DashboardEventBus"/>：同 process 內的 Blazor 元件透過 C# event 直接收。</item>
/// </list>
/// 這樣 Blazor 不需要再當自己的 SignalR Client 連回 localhost，減一層序列化 + WebSocket 開銷。
/// </summary>
public sealed class SignalRRealtimeBroadcaster : IRealtimeBroadcaster
{
    private readonly IHubContext<TradeHub> _hub;
    private readonly DashboardEventBus _bus;

    public SignalRRealtimeBroadcaster(IHubContext<TradeHub> hub, DashboardEventBus bus)
    {
        _hub = hub;
        _bus = bus;
    }

    public async Task BroadcastTradeAsync(TradeFilledUpdate update, CancellationToken ct = default)
    {
        _bus.RaiseTrade(update);
        await _hub.Clients.All.SendAsync("TradeFilled", update, ct).ConfigureAwait(false);
    }

    public async Task BroadcastStatsAsync(DashboardStatsUpdate update, CancellationToken ct = default)
    {
        _bus.RaiseStats(update);
        await _hub.Clients.All.SendAsync("StatsUpdate", update, ct).ConfigureAwait(false);
    }

    public async Task BroadcastPositionClosedAsync(PositionClosedUpdate update, CancellationToken ct = default)
    {
        _bus.RaisePositionClosed(update);
        await _hub.Clients.All.SendAsync("PositionClosed", update, ct).ConfigureAwait(false);
    }

    public async Task BroadcastStrategyEvaluatedAsync(StrategyEvaluatedUpdate update, CancellationToken ct = default)
    {
        _bus.RaiseStrategyEvaluated(update);
        await _hub.Clients.All.SendAsync("StrategyEvaluated", update, ct).ConfigureAwait(false);
    }

    public async Task BroadcastPositionPnLAsync(PositionPnLTickUpdate update, CancellationToken ct = default)
    {
        _bus.RaisePositionPnL(update);
        await _hub.Clients.All.SendAsync("PositionPnL", update, ct).ConfigureAwait(false);
    }

    public async Task BroadcastStrategyEvaluationFailedAsync(StrategyEvaluationFailedUpdate update, CancellationToken ct = default)
    {
        _bus.RaiseStrategyEvaluationFailed(update);
        await _hub.Clients.All.SendAsync("StrategyEvaluationFailed", update, ct).ConfigureAwait(false);
    }

    public async Task BroadcastStrategyMetadataChangedAsync(StrategyMetadataChangedUpdate update, CancellationToken ct = default)
    {
        _bus.RaiseStrategyMetadataChanged(update);
        await _hub.Clients.All.SendAsync("StrategyMetadataChanged", update, ct).ConfigureAwait(false);
    }

    public async Task BroadcastReconciliationCriticalAsync(ReconciliationCriticalUpdate update, CancellationToken ct = default)
    {
        _bus.RaiseReconciliationCritical(update);
        await _hub.Clients.All.SendAsync("ReconciliationCritical", update, ct).ConfigureAwait(false);
    }
}
