using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 歷史 K 線下載器 — 由 Infrastructure 層透過交易所 REST API 實作。
///
/// 約定：
/// - 回傳按 <see cref="Kline.OpenTime"/> 由舊至新遞增排序，且時間全部介於 [start, end] 內。
/// - 分頁由實作端處理（BingX 單次上限 ~1000 根）；呼叫方不需關心游標。
/// - 以串流方式逐批吐出，避免一次性載入整段區間到記憶體。
/// </summary>
public interface IHistoricalDataProvider
{
    /// <summary>
    /// 下載指定區間內全部 K 線，分批 yield 回呼叫方。
    /// </summary>
    /// <param name="symbol">交易對</param>
    /// <param name="interval">K 線週期</param>
    /// <param name="start">區間起點（含，UTC）</param>
    /// <param name="end">區間終點（含，UTC）</param>
    /// <param name="ct">取消權杖</param>
    IAsyncEnumerable<IReadOnlyList<Kline>> DownloadAsync(
        Symbol symbol,
        KlineInterval interval,
        DateTime start,
        DateTime end,
        CancellationToken ct = default);
}
