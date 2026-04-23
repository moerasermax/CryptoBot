using CryptoBot.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Infrastructure.Persistence;

/// <summary>
/// EF Core / SQLite 版本的 UnitOfWork。
///
/// 一次只允許一個顯式 transaction（呼叫 Begin 後再 Begin 會丟例外，避免巢狀混亂）。
/// 大多數情境只要 SaveChangesAsync 即可，EF 內部會包一個隱式交易；
/// 顯式 Begin/Commit 留給跨 Repository 的多步原子操作。
/// </summary>
public sealed class UnitOfWork : IUnitOfWork, IAsyncDisposable
{
    private readonly AppDbContext _ctx;
    private readonly ILogger<UnitOfWork>? _logger;
    private IDbContextTransaction? _currentTx;

    public UnitOfWork(AppDbContext ctx, ILogger<UnitOfWork>? logger = null)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        _ctx.SaveChangesAsync(ct);

    /// <summary>
    /// S53 T1：樂觀併發重試。短暫的 DbUpdateConcurrencyException 通常源自「同一列被
    /// WS 事件處理器與策略迴圈同時更新」（例：AccountSynchronizer 剛從 WS 收到 order
    /// fill 完成 UPDATE，StrategyExecutor 這邊才 SaveChanges）。對金融系統而言
    /// 「訂單紀錄落地」比「誰先寫完」更重要 — 因此採 <b>client-wins</b>：
    /// 把 OriginalValues 校準到 DB 當前值，CurrentValues 維持 client 改動，然後重試。
    /// <para>
    /// 若衝突 entry 對應的 row 已被刪除（GetDatabaseValuesAsync 回 null），
    /// state 強制 Detached — 視為已被其他流程清掉，不再嘗試寫回。
    /// </para>
    /// <para>
    /// <b>為什麼用加法上限而非指數退避</b>：SQLite 單寫入者、衝突視窗通常 &lt;10ms，
    /// 指數退避只會拉長延遲；3 次內沒搞定代表不是「競爭」而是「流程設計錯」，這時 propagate 出去才是對的。
    /// </para>
    /// </summary>
    public async Task<int> SaveChangesWithRetryAsync(int maxAttempts = 3, CancellationToken ct = default)
    {
        if (maxAttempts < 1) maxAttempts = 1;

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await _ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                _logger?.LogWarning(
                    "Concurrency conflict on SaveChanges (attempt {Attempt}/{Max}) — reloading {Count} entries and retrying.",
                    attempt, maxAttempts, ex.Entries.Count);

                foreach (var entry in ex.Entries)
                {
                    var dbValues = await entry.GetDatabaseValuesAsync(ct).ConfigureAwait(false);
                    if (dbValues is null)
                    {
                        // Row 已被其他交易刪除 — client 的改動無處可落，放棄這個 entry。
                        entry.State = EntityState.Detached;
                        continue;
                    }
                    // client-wins：OriginalValues 對齊 DB，CurrentValues 留下 client 想要寫入的內容。
                    entry.OriginalValues.SetValues(dbValues);
                }
            }
        }
    }

    public async Task BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTx is not null)
            throw new InvalidOperationException(
                "A transaction is already in progress. Commit or rollback before starting a new one.");

        _currentTx = await _ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTx is null)
            throw new InvalidOperationException("No active transaction to commit.");

        try
        {
            await _ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await _currentTx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeCurrentTxAsync().ConfigureAwait(false);
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTx is null) return;

        try
        {
            await _currentTx.RollbackAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeCurrentTxAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeCurrentTxAsync()
    {
        if (_currentTx is null) return;
        await _currentTx.DisposeAsync().ConfigureAwait(false);
        _currentTx = null;
    }

    public ValueTask DisposeAsync() => DisposeCurrentTxAsync().AsValueTask();
}

internal static class TaskExtensions
{
    public static ValueTask AsValueTask(this Task task) => new(task);
}
