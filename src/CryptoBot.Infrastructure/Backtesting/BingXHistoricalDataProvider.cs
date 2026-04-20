using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace CryptoBot.Infrastructure.Backtesting;

/// <summary>
/// BingX REST 歷史 K 線下載器。
///
/// 分頁策略：每批次最多抓 <see cref="BatchLimit"/> 根，以「已抓到的最後一根 + 一個週期」作為下一批 startTime。
/// 若單批回傳 0 根或時間未推進，視為終止條件避免無窮迴圈。
/// </summary>
public sealed class BingXHistoricalDataProvider : IHistoricalDataProvider
{
    /// <summary>單批上限。BingX 官方允許 1..1440；保留 1000 給 user 留 buffer。</summary>
    public const int BatchLimit = 1000;

    private readonly IExchangeClient _exchange;
    private readonly ILogger<BingXHistoricalDataProvider> _logger;

    public BingXHistoricalDataProvider(
        IExchangeClient exchange,
        ILogger<BingXHistoricalDataProvider> logger)
    {
        _exchange = exchange;
        _logger = logger;
    }

    public async IAsyncEnumerable<IReadOnlyList<Kline>> DownloadAsync(
        Symbol symbol,
        KlineInterval interval,
        DateTime start,
        DateTime end,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (end <= start)
            throw new ArgumentException($"End ({end:O}) must be after start ({start:O}).", nameof(end));

        var step = interval.ToTimeSpan();
        var cursor = start;
        var batchIndex = 0;

        _logger.LogInformation(
            "📥 [HIST-DL] Start {Symbol} {Interval} from {Start:yyyy-MM-dd HH:mm} to {End:yyyy-MM-dd HH:mm}",
            symbol.BingXFormat, interval, start, end);

        while (cursor <= end && !ct.IsCancellationRequested)
        {
            var batch = await _exchange.GetKlinesAsync(
                symbol,
                interval,
                limit: BatchLimit,
                startTime: cursor,
                endTime: end,
                ct: ct).ConfigureAwait(false);

            if (batch.Count == 0)
            {
                _logger.LogInformation(
                    "📥 [HIST-DL] Batch #{Batch} empty at cursor {Cursor:yyyy-MM-dd HH:mm} — done.",
                    batchIndex, cursor);
                yield break;
            }

            var last = batch[^1].OpenTime;
            batchIndex++;
            _logger.LogInformation(
                "📥 [HIST-DL] Batch #{Batch}: {Count} klines [{First:MM-dd HH:mm} .. {Last:MM-dd HH:mm}]",
                batchIndex, batch.Count, batch[0].OpenTime, last);

            yield return batch;

            var next = last + step;
            if (next <= cursor)
            {
                _logger.LogWarning(
                    "📥 [HIST-DL] Cursor did not advance (cursor={Cursor:O}, next={Next:O}); terminating to avoid infinite loop.",
                    cursor, next);
                yield break;
            }

            cursor = next;
        }
    }
}
