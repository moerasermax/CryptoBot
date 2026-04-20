using CryptoBot.Domain.Repositories;
using Microsoft.EntityFrameworkCore.Storage;

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
    private IDbContextTransaction? _currentTx;

    public UnitOfWork(AppDbContext ctx) => _ctx = ctx;

    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        _ctx.SaveChangesAsync(ct);

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
