using CryptoBot.Application.Backtesting;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace CryptoBot.Infrastructure.Backtesting.Persistence;

/// <summary>
/// EF Core + SQLite 版本的 <see cref="IHistoricalKlineStore"/>。
///
/// 寫入語意：
/// - 以 (Symbol, Interval, OpenTime) 查找既有紀錄，有則覆寫、無則插入；在單一 <see cref="DbContext"/> 實例中一次性 SaveChanges。
/// - 重複下載同區間不會產生重複列，實現冪等。
/// - S23：寫入前檢查批次內相鄰 OpenTime 差是否等於 interval step；有斷層以 Warning log。
///
/// 讀取語意：
/// - <see cref="StreamRangeAsync"/> 使用 <c>AsNoTracking</c> + <c>AsAsyncEnumerable</c>，
///   讓 BacktestEngine 能逐根重播上 TB 的歷史資料而不炸記憶體。
/// </summary>
public sealed class EfHistoricalKlineStore : IHistoricalKlineStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<EfHistoricalKlineStore> _logger;

    public EfHistoricalKlineStore(AppDbContext db, ILogger<EfHistoricalKlineStore>? logger = null)
    {
        _db = db;
        _logger = logger ?? NullLogger<EfHistoricalKlineStore>.Instance;
    }

    public async Task UpsertAsync(
        Symbol symbol,
        KlineInterval interval,
        IEnumerable<Kline> klines,
        CancellationToken ct = default)
    {
        var symbolKey = symbol.BingXFormat;
        var incoming = klines
            .Select(k => HistoricalKlineRecord.FromKline(symbol, k))
            .OrderBy(k => k.OpenTime)
            .ToList();

        if (incoming.Count == 0) return;

        // S23：連續性檢查 — 排序後相鄰 OpenTime 差不等於一個 interval step 即為 gap。
        // 僅 log，不中止寫入（部分可用資料仍應落地，交由上層決定是否重抓）。
        DetectAndLogGaps(symbolKey, interval, incoming);

        var times = incoming.Select(k => k.OpenTime).ToHashSet();

        var existing = await _db.HistoricalKlines
            .Where(x => x.Symbol == symbolKey
                     && x.Interval == interval
                     && times.Contains(x.OpenTime))
            .ToDictionaryAsync(x => x.OpenTime, ct)
            .ConfigureAwait(false);

        foreach (var record in incoming)
        {
            if (existing.TryGetValue(record.OpenTime, out var tracked))
            {
                _db.Entry(tracked).CurrentValues.SetValues(record);
            }
            else
            {
                _db.HistoricalKlines.Add(record);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void DetectAndLogGaps(
        string symbolKey,
        KlineInterval interval,
        IReadOnlyList<HistoricalKlineRecord> sorted)
    {
        if (sorted.Count < 2) return;

        var step = interval.ToTimeSpan();
        for (var i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1].OpenTime;
            var curr = sorted[i].OpenTime;
            var gap = curr - prev;

            if (gap == step) continue;

            // OneMonth 用近似 30 天；交易所以自然月推進，允許 ±3 天誤差不判定為 gap。
            if (interval == KlineInterval.OneMonth &&
                gap >= TimeSpan.FromDays(27) && gap <= TimeSpan.FromDays(33)) continue;

            var missingBars = (long)Math.Round((gap - step).TotalSeconds / step.TotalSeconds);
            _logger.LogWarning(
                "🕳️  [HIST-GAP] {Symbol} {Interval}: gap between {Prev:yyyy-MM-dd HH:mm} and {Curr:yyyy-MM-dd HH:mm} " +
                "(expected step {Step}, observed {Gap}, ~{Missing} bars missing).",
                symbolKey, interval, prev, curr, step, gap, missingBars);
        }
    }

    public async Task<int> CountRangeAsync(
        Symbol symbol,
        KlineInterval interval,
        DateTime start,
        DateTime end,
        CancellationToken ct = default)
    {
        var symbolKey = symbol.BingXFormat;
        return await _db.HistoricalKlines
            .AsNoTracking()
            .Where(x => x.Symbol == symbolKey
                     && x.Interval == interval
                     && x.OpenTime >= start
                     && x.OpenTime <= end)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Kline> StreamRangeAsync(
        Symbol symbol,
        KlineInterval interval,
        DateTime start,
        DateTime end,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var symbolKey = symbol.BingXFormat;
        var query = _db.HistoricalKlines
            .AsNoTracking()
            .Where(x => x.Symbol == symbolKey
                     && x.Interval == interval
                     && x.OpenTime >= start
                     && x.OpenTime <= end)
            .OrderBy(x => x.OpenTime)
            .AsAsyncEnumerable();

        await foreach (var record in query.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return record.ToKline();
        }
    }

    public async Task<(DateTime? Earliest, DateTime? Latest)> GetStoredRangeAsync(
        Symbol symbol,
        KlineInterval interval,
        CancellationToken ct = default)
    {
        var symbolKey = symbol.BingXFormat;

        // 單趟 DB 來回同時取 min/max — SQLite 會對 (Symbol, Interval, OpenTime) 複合索引做兩次範圍掃描尾端。
        var bounds = await _db.HistoricalKlines
            .AsNoTracking()
            .Where(x => x.Symbol == symbolKey && x.Interval == interval)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Min = (DateTime?)g.Min(x => x.OpenTime),
                Max = (DateTime?)g.Max(x => x.OpenTime),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return bounds is null ? (null, null) : (bounds.Min, bounds.Max);
    }
}
