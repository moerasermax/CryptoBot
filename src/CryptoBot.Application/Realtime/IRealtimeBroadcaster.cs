namespace CryptoBot.Application.Realtime;

/// <summary>
/// 把交易事件 / 儀表板狀態推給前端（WebSocket / SignalR）的抽象介面。
///
/// Application 層不依賴 SignalR — 只定義合約；具體推播實作住在 ConsoleApp（Web host）。
/// 預設註冊 <see cref="NullRealtimeBroadcaster"/>（不做事），確保沒裝 Web UI 的環境（純 CLI、
/// 單元測試）也能照跑。Web 啟動時由 ConsoleApp 的 DI Replace 成 SignalR 版本。
/// </summary>
public interface IRealtimeBroadcaster
{
    Task BroadcastTradeAsync(TradeFilledUpdate update, CancellationToken ct = default);
    Task BroadcastStatsAsync(DashboardStatsUpdate update, CancellationToken ct = default);

    /// <summary>S31：已平倉事件 — 「交易歷史表」即時刷新用。</summary>
    Task BroadcastPositionClosedAsync(PositionClosedUpdate update, CancellationToken ct = default);

    /// <summary>S42：策略評估心跳 — Dashboard「Last Evaluated」時間戳 + 呼吸燈動畫用。</summary>
    Task BroadcastStrategyEvaluatedAsync(StrategyEvaluatedUpdate update, CancellationToken ct = default);

    /// <summary>S42：部位即時盈虧跳動 — 開倉部位隨 MarkPrice WS 推播跳動用。</summary>
    Task BroadcastPositionPnLAsync(PositionPnLTickUpdate update, CancellationToken ct = default);

    /// <summary>S44：策略評估失敗事件 — 滾動決策日誌顯示 [ERROR] 色碼用。</summary>
    Task BroadcastStrategyEvaluationFailedAsync(StrategyEvaluationFailedUpdate update, CancellationToken ct = default);

    /// <summary>
    /// S45-S48：策略「身分」異動（Name / StrategyType / Symbol / Interval / Leverage）— 由
    /// <c>/api/lab/apply</c> 在熱套用後立即觸發，Dashboard 卡片不必等下一個 K 線心跳就能跳轉。
    /// </summary>
    Task BroadcastStrategyMetadataChangedAsync(StrategyMetadataChangedUpdate update, CancellationToken ct = default);
}
