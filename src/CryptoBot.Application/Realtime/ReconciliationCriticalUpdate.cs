namespace CryptoBot.Application.Realtime;

/// <summary>
/// S72 [CRITICAL_SYNC]：對帳階段需要使用者注意的關鍵事件 — 包括「實證查無、降級走 Unaccounted」
/// 與「殭屍訂單強制補殺」兩類。配合 <c>ILogger.LogError</c> 雙軌（broadcast + log）通知，
/// 確保 IM §S72 紀律「對帳異常必廣播」落地、避免靜默結算（IRON ⑤）。
///
/// <para>
/// 接收端（Dashboard / 外部 SignalR client）應顯著呈現此事件 — 不同於日常 PnL 跳動，
/// 這代表系統真實狀態與紀錄之間出現了需要人工介入的偏差。
/// </para>
/// </summary>
/// <param name="OccurredAtUtc">事件發生 UTC 時間。</param>
/// <param name="Category">事件分類：<c>"PositionUnaccounted"</c>（部位消失但查無成交實證）/ <c>"ZombieOrder"</c>（殭屍訂單強制補殺）/ <c>"ReconcileFailed"</c>（對帳流程本身錯誤）。</param>
/// <param name="Symbol">受影響的交易對；無對應時可為空字串。</param>
/// <param name="EntityId">受影響的 Domain entity Id（Position / Order GUID）— 字串以避免序列化 Guid 大小寫差異。</param>
/// <param name="Detail">人類可讀描述（含「為何降級」「採取何種隱式約定」等線索）。</param>
public sealed record ReconciliationCriticalUpdate(
    DateTime OccurredAtUtc,
    string Category,
    string Symbol,
    string EntityId,
    string Detail);
