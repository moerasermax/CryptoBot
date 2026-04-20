using CryptoBot.Application.Backtesting;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;

namespace CryptoBot.Infrastructure.Backtesting.Persistence;

/// <summary>
/// EF Core + SQLite 版本的 <see cref="IHistoricalKlineStore"/>。
///
/// 寫入語意：
/// - 以 (Symbol, Interval, OpenTime) 查找既有紀錄，有則覆寫、無則插入；在單一 <see cref="DbContext"/> 實例中一次性 SaveChanges。
/// - 重複下載同區間不會產生重複列，實現冪等。
///
/// 讀取語意：
/// - <see cref="StreamRangeAsync"/> 使用 <c>AsNoTracking</c> + <c>AsAsyncEnumerable</c>，
///   讓 BacktestEngine 能逐根重播上 TB 的歷史資料而不炸記憶體。
/// </summary>
public sealed class EfHistoricalKlineStore : IHistoricalKlineStore
{
    private readonly AppDbContext _db;

    public EfHistoricalKlineStore(AppDbContext db)
    {
        _db = db;
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
            .ToList();

        if (incoming.Count == 0) return;

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

    public async Task<DateTime?> GetLatestOpenTimeAsync(
        Symbol symbol,
        KlineInterval interval,
        CancellationToken ct = default)
    {
        var symbolKey = symbol.BingXFormat;
        var latest = await _db.HistoricalKlines
            .AsNoTracking()
            .Where(x => x.Symbol == symbolKey && x.Interval == interval)
            .OrderByDescending(x => x.OpenTime)
            .Select(x => (DateTime?)x.OpenTime)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return latest;
    }
}
