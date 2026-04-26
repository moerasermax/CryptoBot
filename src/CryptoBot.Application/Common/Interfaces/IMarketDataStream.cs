using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Common.Interfaces;

/// <summary>
/// 行情資料流介面 - 抽象 WebSocket 訂閱。
///
/// 事件分成兩大家族：
/// - 公開行情（K 線、價格）— 任何人都能訂閱
/// - 私有帳戶（訂單狀態、帳戶快照）— 需要 API 金鑰 + listenKey
///
/// 帳戶事件採用交易所無關的 DTO（<see cref="ExchangeOrderUpdate"/> / <see cref="ExchangeAccountUpdate"/>），
/// 讓 AccountSynchronizer 不必依賴 BingX SDK 型別。
/// </summary>
public interface IMarketDataStream : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 熱切換到新模式。實作必須：先 <see cref="StopAsync"/>（關 WS / 清 listenKey），
    /// 用新 endpoint 重建 socket client，但**不**自動 <see cref="StartAsync"/> —
    /// 上層 <c>EnvironmentSwitcher</c> 會明確指定何時重啟，以免在
    /// 沒有任何訂閱者的狀態下白開連線。
    /// </summary>
    Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default);

    /// <summary>訂閱 K 線更新</summary>
    Task SubscribeKlinesAsync(
        Symbol symbol, KlineInterval interval, CancellationToken ct = default);

    /// <summary>訂閱最新價</summary>
    Task SubscribeMarkPriceAsync(Symbol symbol, CancellationToken ct = default);

    /// <summary>取消訂閱</summary>
    Task UnsubscribeAsync(Symbol symbol, CancellationToken ct = default);

    /// <summary>K 線更新事件</summary>
    event Func<Symbol, KlineInterval, Kline, Task>? OnKlineUpdate;

    /// <summary>最新價更新事件</summary>
    event Func<Symbol, Price, Task>? OnPriceUpdate;

    /// <summary>訂單狀態更新事件（user-data WebSocket）</summary>
    event Func<ExchangeOrderUpdate, Task>? OnExchangeOrderUpdate;

    /// <summary>帳戶快照更新事件（user-data WebSocket — 含餘額與持倉快照）</summary>
    event Func<ExchangeAccountUpdate, Task>? OnExchangeAccountUpdate;
}

/// <summary>
/// 訂單狀態更新 DTO — 交易所無關。
/// <see cref="ExchangeOrderId"/> 用來與 Order aggregate 的 <c>ExchangeOrderId</c> 對應。
/// </summary>
public sealed record ExchangeOrderUpdate(
    string ExchangeOrderId,
    Symbol Symbol,
    OrderStatus Status,
    decimal Quantity,
    decimal QuantityFilled,
    decimal? AverageFillPrice,
    decimal Fee,
    DateTime UpdateTime);

/// <summary>
/// 帳戶快照更新 DTO — 交易所無關。
/// 餘額 + 該次推送的所有持倉狀態；持倉 Quantity 為 0 表示已平倉。
/// </summary>
public sealed record ExchangeAccountUpdate(
    DateTime UpdateTime,
    IReadOnlyList<ExchangeBalanceEntry> Balances,
    IReadOnlyList<ExchangePositionInfo> Positions);

/// <summary>
/// 帳戶餘額項目 — 每個資產一筆。
/// </summary>
public sealed record ExchangeBalanceEntry(
    string Asset,
    decimal Balance,
    decimal UnrealizedProfit);

/// <summary>
/// 通知介面 - 抽象 Discord/Telegram/Email 等
/// </summary>
public interface INotificationService
{
    Task NotifyAsync(string title, string message, NotificationLevel level = NotificationLevel.Info,
                     CancellationToken ct = default);
    Task NotifyTradeAsync(string symbol, string action, decimal price, decimal quantity,
                          CancellationToken ct = default);
    Task NotifyErrorAsync(Exception ex, CancellationToken ct = default);

    /// <summary>
    /// S28 T1：日損熔斷觸發時的專用通道 — Discord 實作會以紫色 embed 呈現，與一般 Critical（紅）
    /// 警報明確區分，讓值班人員在訊息串中一眼看到「風險閘門已觸發」。
    /// </summary>
    Task NotifyCircuitBreakerAsync(string reason, CancellationToken ct = default);
}

public enum NotificationLevel
{
    Info,
    Warning,
    Error,
    Critical
}
