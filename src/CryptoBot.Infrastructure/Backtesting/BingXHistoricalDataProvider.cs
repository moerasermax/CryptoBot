using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Exchange.BingX;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace CryptoBot.Infrastructure.Backtesting;

/// <summary>
/// BingX REST 歷史 K 線下載器。
///
/// 分頁策略：每批次最多抓 <see cref="BatchLimit"/> 根，以「已抓到的最後一根 + 一個週期」作為下一批 startTime。
/// 若單批回傳 0 根或時間未推進，視為終止條件避免無窮迴圈。
///
/// S23 v2.0：為單批呼叫包上退避重試（處理 429 / 5xx / 網路抖動），並在 log 中標註年月跨度。
/// 重試邏輯限於 <see cref="FetchBatchWithRetryAsync"/>；caller 透過 <paramref name="ct"/> 取消時
/// 直接往上拋，不進入重試迴圈。
/// </summary>
public sealed class BingXHistoricalDataProvider : IHistoricalDataProvider
{
    /// <summary>單批上限。BingX 官方允許 1..1440；保留 1000 給 user 留 buffer。</summary>
    public const int BatchLimit = 1000;

    /// <summary>每批呼叫最多嘗試次數（包含首次）。</summary>
    internal const int MaxAttempts = 5;

    /// <summary>首次退避秒數；之後以 2 倍遞增，上限 <see cref="MaxBackoff"/>。</summary>
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>退避上限 — 避免指數爆炸。</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(16);

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
            "📥 [HIST-DL] Start {Symbol} {Interval} span {Start:yyyy-MM} .. {End:yyyy-MM} ({Days:N0} days)",
            symbol.BingXFormat, interval, start, end, (end - start).TotalDays);

        while (cursor <= end && !ct.IsCancellationRequested)
        {
            var batch = await FetchBatchWithRetryAsync(symbol, interval, cursor, end, batchIndex + 1, ct)
                .ConfigureAwait(false);

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
                "📥 [HIST-DL] Batch #{Batch}: {Count} klines [{First:yyyy-MM-dd HH:mm} .. {Last:yyyy-MM-dd HH:mm}]",
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

    /// <summary>
    /// 包一層退避重試：<see cref="ExchangeApiException"/>（429 / 5xx 由 BingXCheckExtensions 翻成此例外）、
    /// 網路錯誤（<see cref="HttpRequestException"/> / <see cref="SocketException"/>）、
    /// 以及非 caller-cancel 的 <see cref="TaskCanceledException"/>（SDK 內部逾時）皆會重試。
    /// 其他例外（程式錯誤、Domain 驗證）直接往上拋。
    ///
    /// CAP-006：每 batch 自己的 endTime = startTime + step × BatchLimit（截到 totalEnd 邊界）。
    /// BingX REST <c>/openApi/swap/v3/quote/klines</c> 對 (startTime, endTime, limit) 同傳時返回
    /// endTime 前最近 limit 根；若直接傳整個 lookback 的 end、第 1 batch 即拿到 [end-limit*step, end]
    /// 約 42 天（1h × 1000）、cursor 推進到 end+step > end → paginate while 第 2 圈終止、永遠只跑 1 batch。
    /// 設 batch 自己的 endTime 強制 batch 範圍 = [cursor, cursor + limit*step]、多輪 paginate
    /// 推進直至覆蓋整個 [start, end]。
    /// </summary>
    private async Task<IReadOnlyList<Kline>> FetchBatchWithRetryAsync(
        Symbol symbol,
        KlineInterval interval,
        DateTime cursor,
        DateTime end,
        int batchNumber,
        CancellationToken ct)
    {
        var backoff = InitialBackoff;

        var step = interval.ToTimeSpan();
        var batchEnd = cursor + step * BatchLimit;
        if (batchEnd > end) batchEnd = end;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await _exchange.GetKlinesAsync(
                    symbol,
                    interval,
                    limit: BatchLimit,
                    startTime: cursor,
                    endTime: batchEnd,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    _logger.LogError(ex,
                        "📥 [HIST-DL] Batch #{Batch} failed after {Attempts} attempts ({Err}).",
                        batchNumber, attempt, ex.GetType().Name);
                    throw;
                }

                _logger.LogWarning(
                    "📥 [HIST-DL] Batch #{Batch} attempt {Attempt}/{Max} failed: {Err} — retry in {Backoff:N1}s.",
                    batchNumber, attempt, MaxAttempts, ex.Message, backoff.TotalSeconds);

                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }

                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds));
            }
        }

        // 理論上不可達：上面的 for 不是拋就是 return。
        throw new InvalidOperationException("Retry loop exited without resolution.");
    }

    // caller-cancel 的判斷在外層 when 子句統一做 (!ct.IsCancellationRequested)，
    // 這裡只負責判斷「例外類型本身是否屬於短暫錯誤」。
    private static bool IsTransient(Exception ex) => ex switch
    {
        ExchangeApiException => true,   // BingX 429 / 5xx / 臨時拒絕都會透過 Check() 翻成這個
        HttpRequestException => true,   // TCP / DNS / TLS 類
        SocketException      => true,
        TaskCanceledException => true,  // SDK 內部 RequestTimeout（若 caller 取消，外層 when 會擋）
        TimeoutException     => true,
        _                    => false,
    };
}
