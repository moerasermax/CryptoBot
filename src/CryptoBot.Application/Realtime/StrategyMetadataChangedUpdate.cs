namespace CryptoBot.Application.Realtime;

/// <summary>
/// S45-S48 VCP-Realtime-Sync：策略「身分」變更的即時推播事件 — 當 /api/lab/apply
/// 同步更新了 Name / StrategyType / Symbol / Interval / Leverage 後，立即廣播。
///
/// <para>
/// 為什麼不依賴 <see cref="StrategyEvaluatedUpdate"/> 心跳：心跳只在下一根 K 線收盤後觸發，
/// 在 15m / 1h 週期下 UI 會等數分鐘到一小時才反映套用結果。套用是使用者主動觸發的「現在」操作，
/// 必須在回應之前就把變更推播出去，Dashboard 卡片才會「瞬間」跳轉。
/// </para>
///
/// <para>
/// 欄位刻意維持 primitive（Symbol/Interval 已攤平為字串）方便 SignalR JSON 序列化。
/// Dashboard 收到後用 <c>with</c>-expression 局部更新 <c>StrategyDto</c>，不需重拉 <c>/api/strategies</c>。
/// </para>
/// </summary>
public sealed record StrategyMetadataChangedUpdate(
    Guid StrategyId,
    string Name,
    string StrategyType,
    string Symbol,
    string Interval,
    int Leverage);
