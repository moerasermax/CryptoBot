using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence.Repositories;

public sealed class PositionRepository : IPositionRepository
{
    private readonly AppDbContext _ctx;

    public PositionRepository(AppDbContext ctx) => _ctx = ctx;

    public Task<Position?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _ctx.Positions.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        var list = await _ctx.Positions
            .Where(p => !p.IsClosed)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Position>> GetOpenPositionsBySymbolAsync(
        Symbol symbol, CancellationToken ct = default)
    {
        // S99 HOTFIX：SIGNAL SELL 後 AccountSynchronizer 會經這裡查對應的本地倉位，
        // 原本用 EF.Property<string>(p, nameof(Position.Symbol)) == symbolStr 在
        // SymbolConverter (ValueConverter<Symbol, string>) 下會拋
        // `InvalidCastException: Invalid cast from 'System.String' to 'Symbol'`
        // — README §S17.5 已記錄相同雷點。
        // 改成先抓所有未平倉（量少、實務上 <50 筆），再在記憶體裡用 Symbol.Equals 過濾，
        // 徹底迴避 EF 對 value-converted VO 的表達式翻譯邊角案例。
        var openList = await _ctx.Positions
            .Where(p => !p.IsClosed)
            .ToListAsync(ct).ConfigureAwait(false);
        return openList.Where(p => p.Symbol.Equals(symbol)).ToList();
    }

    public async Task<IReadOnlyList<Position>> GetByStrategyIdAsync(
        Guid strategyId, bool includeClosedPositions = false, CancellationToken ct = default)
    {
        var query = _ctx.Positions.Where(p => p.StrategyId == strategyId);
        if (!includeClosedPositions)
            query = query.Where(p => !p.IsClosed);

        var list = await query.ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Position>> GetClosedPositionsInRangeAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var list = await _ctx.Positions
            .Where(p => p.IsClosed
                     && p.ClosedAt != null
                     && p.ClosedAt >= from
                     && p.ClosedAt < to)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<Position>> GetRecentClosedAsync(
        int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<Position>();
        var list = await _ctx.Positions
            .Where(p => p.IsClosed && p.ClosedAt != null)
            .OrderByDescending(p => p.ClosedAt)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return list;
    }

    public Task AddAsync(Position position, CancellationToken ct = default)
    {
        _ctx.Positions.Add(position);
        return Task.CompletedTask;
    }

    /// <summary>
    /// S53 T3：縮小事務邊界。
    /// <para>
    /// 如果 entity 已被目前的 <see cref="AppDbContext"/> tracking（絕大多數場景 — Repository 先
    /// <c>GetOpenPositionsAsync</c> 之類方法把它拉進 context），就不要呼叫
    /// <c>DbSet.Update()</c>，因為那會把「所有欄位」標為 Modified，產出整行 UPDATE；
    /// 讓 EF 自家 Change Tracker 針對實際變動欄位自動偵測，這樣一次 MarkPrice tick
    /// 只會 UPDATE <c>CurrentPrice</c>（若啟用追蹤停損 + <c>StopLossPrice</c>），其他 15+ 欄位都不動，
    /// 與 AccountSynchronizer / StrategyExecutor 的併發 UPDATE 衝突窗口自然縮小。
    /// </para>
    /// <para>
    /// Detached 時仍 reattach 一次（API / 跨 scope 硬塞進來的 detached entity 才會走這支路徑）。
    /// </para>
    /// </summary>
    public Task UpdateAsync(Position position, CancellationToken ct = default)
    {
        var entry = _ctx.Entry(position);
        if (entry.State == EntityState.Detached)
            _ctx.Positions.Update(position);
        return Task.CompletedTask;
    }
}
