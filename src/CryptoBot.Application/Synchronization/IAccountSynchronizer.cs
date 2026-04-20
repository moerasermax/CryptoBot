namespace CryptoBot.Application.Synchronization;

/// <summary>
/// 帳戶同步器 — 交易所側（WebSocket + REST）⇄ 本地 Repository 的單向對帳通道。
///
/// 兩種同步管道：
/// 1. 即時 — 訂閱 <c>IMarketDataStream.OnExchangeOrderUpdate/OnExchangeAccountUpdate</c>
///    即時把訂單狀態與倉位變化寫回本地 aggregate。
/// 2. 批次 — <see cref="ReconcileAsync"/> 於啟動時呼叫 REST 補齊 WS 漏接的變化
///    （例如啟動前就已成交的訂單、被外部平倉的倉位）。
/// </summary>
public interface IAccountSynchronizer
{
    /// <summary>訂閱 user-data WS 事件。幂等 — 重複呼叫只掛一次 handler。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>取消訂閱事件。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>啟動時或復原時的一次性對帳。拉 REST 快照、比對本地、補差異。</summary>
    Task ReconcileAsync(CancellationToken ct = default);
}
