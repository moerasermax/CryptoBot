namespace CryptoBot.Application.Realtime;

/// <summary>
/// 已平倉事件 — 供「交易歷史表」做即時刷新的訊號。
///
/// 發送時機：<c>AccountSynchronizer</c> 透過 WS 偵測到遠端倉位歸零並成功呼叫 <c>Position.Close()</c> 後。
/// 負載維持 primitive 欄位以便 SignalR 序列化，不塞 Domain 物件。
/// </summary>
public sealed record PositionClosedUpdate(
    Guid PositionId,
    DateTime ClosedAtUtc,
    string Symbol,
    string PositionSide,   // "Long" / "Short"
    decimal ExitPrice,
    decimal RealizedPnL);
