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
}
