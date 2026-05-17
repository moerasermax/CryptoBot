using CryptoBot.Application.Common.DomainEvents;
using CryptoBot.Application.Common.Exceptions;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Common;
using CryptoBot.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    private readonly IDomainEventDispatcher? _eventDispatcher;
    private IDbContextTransaction? _currentTx;

    public UnitOfWork(AppDbContext ctx, ILogger<UnitOfWork>? logger = null, IDomainEventDispatcher? eventDispatcher = null)
    {
        _ctx = ctx;
        _logger = logger;
        _eventDispatcher = eventDispatcher;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            // S77 Bug 12: 收集 pending IDomainEvent before SaveChanges (after SaveChanges aggregate Id 才 stable)
            var pendingEvents = CollectAndClearDomainEvents();
            var result = await _ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            // SaveChanges 成功後 dispatch events (避免 in-transaction 副作用 / 失敗回滾整 transaction)
            if (_eventDispatcher is not null && pendingEvents.Count > 0)
            {
                await _eventDispatcher.DispatchAsync(pendingEvents, ct).ConfigureAwait(false);
            }
            return result;
        }
        catch (DbUpdateException ex) when (TryExtractDuplicateClientOrderId(ex, out var clientOrderId))
        {
            throw new DuplicateClientOrderIdException(clientOrderId, ex);
        }
    }

    /// <summary>
    /// S77 Bug 12: 從 EF ChangeTracker 收集所有 AggregateRoot.DomainEvents、清空 entity 上的 list、
    ///             回傳 events 供 dispatcher 在 SaveChanges 後 publish。
    /// </summary>
    private IReadOnlyList<IDomainEvent> CollectAndClearDomainEvents()
    {
        var aggregates = _ctx.ChangeTracker.Entries()
            .Select(e => e.Entity)
            .OfType<IAggregateRootWithEvents>()
            .Where(ar => ar.DomainEvents.Count > 0)
            .ToList();

        if (aggregates.Count == 0) return Array.Empty<IDomainEvent>();

        var events = aggregates.SelectMany(ar => ar.DomainEvents).ToList();
        foreach (var ar in aggregates) ar.ClearDomainEvents();
        return events;
    }

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
            catch (DbUpdateException ex) when (TryExtractDuplicateClientOrderId(ex, out var clientOrderId))
            {
                // S66-A：唯一索引衝突屬「業務可預期狀態」，不重試也不再拋 EF 例外，
                // 改丟 DuplicateClientOrderIdException 讓 Application 層走自癒分支
                // （查交易所端狀態 → 對齊本地）。
                throw new DuplicateClientOrderIdException(clientOrderId, ex);
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

    /// <summary>
    /// S66-A：在 SaveChanges 噴出的 <see cref="DbUpdateException"/> 中嗅探「ClientOrderId
    /// 唯一索引衝突」。命中時回 <c>true</c> 並回填 <paramref name="clientOrderId"/>，
    /// 由呼叫端轉拋 <see cref="DuplicateClientOrderIdException"/>。
    ///
    /// 偵測順序：
    ///   1. 先看 <c>SqliteException.SqliteErrorCode == 19</c>（SQLITE_CONSTRAINT），訊息含 "ClientOrderId"
    ///   2. 再看 <see cref="DbUpdateException.Entries"/> 中是否為 <see cref="Order"/> 且其
    ///      <see cref="Order.ClientOrderId"/> 已被其他列佔用
    /// </summary>
    private bool TryExtractDuplicateClientOrderId(DbUpdateException ex, out string clientOrderId)
    {
        clientOrderId = string.Empty;

        if (ex.InnerException is SqliteException sqliteEx
            && sqliteEx.SqliteErrorCode == 19
            && sqliteEx.Message.Contains("ClientOrderId", StringComparison.OrdinalIgnoreCase))
        {
            var orderEntry = ex.Entries.FirstOrDefault(e => e.Entity is Order);
            if (orderEntry?.Entity is Order order && !string.IsNullOrEmpty(order.ClientOrderId))
            {
                clientOrderId = order.ClientOrderId;
                return true;
            }
        }

        return false;
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
