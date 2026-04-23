namespace CryptoBot.Application.Realtime;

/// <summary>
/// S42 T3：開倉部位即時盈虧更新 — Dashboard「Active Positions」表格隨行情跳動用。
///
/// 發送時機：<c>AccountSynchronizer</c> 收到 WS account update 且 remote.MarkPrice 有更新時，
/// 在 <c>Position.UpdateCurrentPrice()</c> 之後 fire。每次行情推播都會帶一筆（每秒多次），
/// UI 端只需找到對應 PositionId 更新那三個欄位，避免整包重拉。
/// </summary>
public sealed record PositionPnLTickUpdate(
    Guid PositionId,
    string Symbol,
    decimal CurrentPrice,
    decimal UnrealizedPnL,
    decimal UnrealizedPnLPercent);
