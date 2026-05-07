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

    /// <summary>S31：Position 已平倉 — TradeHistoryTable 自動刷新用。</summary>
    public event Action<PositionClosedUpdate>? PositionClosed;

    public event Action<OptimizationProgressUpdate>? OptimizationProgress;
    public event Action<OptimizationCompletedUpdate>? OptimizationCompleted;
    public event Action<OptimizationFailedUpdate>? OptimizationFailed;

    /// <summary>S27：交易所 REST latency 心跳。</summary>
    public event Action<ExchangeHealthUpdate>? ExchangeHealthUpdated;

    /// <summary>S28 T1：日損熔斷狀態變化（trip / reset）— UI 解鎖/上鎖 toggle 時用。</summary>
    public event Action<BreakerStateUpdate>? BreakerStateChanged;

    /// <summary>S42 T1：策略評估心跳 — 「Last Evaluated」時間戳 + 呼吸燈用。</summary>
    public event Action<StrategyEvaluatedUpdate>? StrategyEvaluated;

    /// <summary>S42 T3：開倉部位即時盈虧跳動。</summary>
    public event Action<PositionPnLTickUpdate>? PositionPnLTicked;

    /// <summary>S44：策略評估失敗事件 — 滾動決策日誌顯示 [ERROR] 色碼用。</summary>
    public event Action<StrategyEvaluationFailedUpdate>? StrategyEvaluationFailed;

    /// <summary>S45-S48：策略身分（Name / Type / Symbol / Interval / Leverage）熱套用後立即跳轉用。</summary>
    public event Action<StrategyMetadataChangedUpdate>? StrategyMetadataChanged;

    /// <summary>S72 [CRITICAL_SYNC]：對帳關鍵事件 — Dashboard 應顯著呈現，提示人工介入。</summary>
    public event Action<ReconciliationCriticalUpdate>? ReconciliationCritical;

    public void RaiseTrade(TradeFilledUpdate update) => TradeFilled?.Invoke(update);
    public void RaiseStats(DashboardStatsUpdate update) => StatsUpdated?.Invoke(update);
    public void RaisePositionClosed(PositionClosedUpdate update) => PositionClosed?.Invoke(update);

    public void RaiseOptimizationProgress(OptimizationProgressUpdate update) =>
        OptimizationProgress?.Invoke(update);
    public void RaiseOptimizationCompleted(OptimizationCompletedUpdate update) =>
        OptimizationCompleted?.Invoke(update);
    public void RaiseOptimizationFailed(OptimizationFailedUpdate update) =>
        OptimizationFailed?.Invoke(update);

    public void RaiseExchangeHealth(ExchangeHealthUpdate update) =>
        ExchangeHealthUpdated?.Invoke(update);

    public void RaiseBreakerState(BreakerStateUpdate update) =>
        BreakerStateChanged?.Invoke(update);

    public void RaiseStrategyEvaluated(StrategyEvaluatedUpdate update) =>
        StrategyEvaluated?.Invoke(update);

    public void RaisePositionPnL(PositionPnLTickUpdate update) =>
        PositionPnLTicked?.Invoke(update);

    public void RaiseStrategyEvaluationFailed(StrategyEvaluationFailedUpdate update) =>
        StrategyEvaluationFailed?.Invoke(update);

    public void RaiseStrategyMetadataChanged(StrategyMetadataChangedUpdate update) =>
        StrategyMetadataChanged?.Invoke(update);

    public void RaiseReconciliationCritical(ReconciliationCriticalUpdate update) =>
        ReconciliationCritical?.Invoke(update);
}
