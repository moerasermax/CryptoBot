namespace CryptoBot.Application.Realtime;

/// <summary>
/// 預設的 NoOp 實作 — 單元測試 / 純 CLI 模式下吞掉所有推播，不做事也不拋例外。
/// Web host 啟動後會被 SignalR 版本 Replace 掉。
/// </summary>
public sealed class NullRealtimeBroadcaster : IRealtimeBroadcaster
{
    public Task BroadcastTradeAsync(TradeFilledUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BroadcastStatsAsync(DashboardStatsUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BroadcastPositionClosedAsync(PositionClosedUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BroadcastStrategyEvaluatedAsync(StrategyEvaluatedUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BroadcastPositionPnLAsync(PositionPnLTickUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BroadcastStrategyEvaluationFailedAsync(StrategyEvaluationFailedUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BroadcastStrategyMetadataChangedAsync(StrategyMetadataChangedUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BroadcastReconciliationCriticalAsync(ReconciliationCriticalUpdate update, CancellationToken ct = default)
        => Task.CompletedTask;
}
