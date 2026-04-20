using CryptoBot.Application.Realtime;

namespace CryptoBot.ConsoleApp.Realtime;

/// <summary>
/// 行程內的事件總線 — Blazor Server 的元件直接訂這個就能收到即時推播，
/// 不必在同一個 process 裡再走一趟 SignalR client ↔ hub 迴圈。
///
/// 外部客戶端（Postman、未來手機 App）仍可透過 <see cref="TradeHub"/> WebSocket 訂閱同一份資料，
/// 兩條路徑都由 <see cref="SignalRRealtimeBroadcaster"/> 同時 fan-out。
///
/// 事件只在 process 內同步 raise — 訂閱者（Blazor component）需確保 handler 不會阻塞太久，
/// 必要時自己 <c>InvokeAsync(StateHasChanged)</c> 切回 UI thread。
/// </summary>
public sealed class DashboardEventBus
{
    public event Action<TradeFilledUpdate>? TradeFilled;
    public event Action<DashboardStatsUpdate>? StatsUpdated;

    public event Action<OptimizationProgressUpdate>? OptimizationProgress;
    public event Action<OptimizationCompletedUpdate>? OptimizationCompleted;
    public event Action<OptimizationFailedUpdate>? OptimizationFailed;

    public void RaiseTrade(TradeFilledUpdate update) => TradeFilled?.Invoke(update);
    public void RaiseStats(DashboardStatsUpdate update) => StatsUpdated?.Invoke(update);

    public void RaiseOptimizationProgress(OptimizationProgressUpdate update) =>
        OptimizationProgress?.Invoke(update);
    public void RaiseOptimizationCompleted(OptimizationCompletedUpdate update) =>
        OptimizationCompleted?.Invoke(update);
    public void RaiseOptimizationFailed(OptimizationFailedUpdate update) =>
        OptimizationFailed?.Invoke(update);
}
