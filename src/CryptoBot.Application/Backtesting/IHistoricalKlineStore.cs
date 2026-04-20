using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 歷史 K 線持久化存儲 — 由 Infrastructure 以 SQLite 實作。
///
/// 支援兩種使用情境：
/// 1. 下載階段：<see cref="UpsertAsync"/> 批次寫入（同 (Symbol, Interval, OpenTime) 採覆寫語意）。
/// 2. 回測階段：<see cref="StreamRangeAsync"/> 按時間區間由舊到新串流讀取，配合 BacktestEngine 的重播迴圈。
///
/// 索引以複合主鍵 (Symbol, Interval, OpenTime) 為主，保證區間查詢是 range scan。
/// </summary>
public interface IHistoricalKlineStore
{
    /// <summary>
    /// 批次寫入（or 覆寫）K 線。冪等：重複呼叫不會產生重複資料。
    /// </summary>
    Task UpsertAsync(
        Symbol symbol,
        KlineInterval interval,
        IEnumerable<Kline> klines,
        CancellationToken ct = default);

    /// <summary>
    /// 查詢區間內 K 線總數，用於 BacktestEngine 事前檢查 / 進度條。
    /// </summary>
    Task<int> CountRangeAsync(
        Symbol symbol,
        KlineInterval interval,
        DateTime start,
        DateTime end,
        CancellationToken ct = default);

    /// <summary>
    /// 按時間區間串流讀取 K 線，由舊至新。實作端應使用 AsNoTracking + AsAsyncEnumerable
    /// 以避免整段載入記憶體。
    /// </summary>
    IAsyncEnumerable<Kline> StreamRangeAsync(
        Symbol symbol,
        KlineInterval interval,
        DateTime start,
        DateTime end,
        CancellationToken ct = default);

    /// <summary>
    /// 取得已存的最新一根 K 線時間（區間未指定）— 用於增量下載：只補齊缺失段。
    /// 若該交易對/週期尚無資料，回傳 null。
    /// </summary>
    Task<DateTime?> GetLatestOpenTimeAsync(
        Symbol symbol,
        KlineInterval interval,
        CancellationToken ct = default);
}
