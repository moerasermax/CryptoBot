using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Repositories;

/// <summary>
/// 訂單儲存庫介面 - Repository Pattern (DDD)
/// </summary>
public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Order?> GetByExchangeOrderIdAsync(string exchangeOrderId, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetActiveOrdersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetByStrategyIdAsync(Guid strategyId, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct = default);
    Task AddAsync(Order order, CancellationToken ct = default);
    Task UpdateAsync(Order order, CancellationToken ct = default);
}

/// <summary>
/// 持倉儲存庫介面
/// </summary>
public interface IPositionRepository
{
    Task<Position?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetOpenPositionsBySymbolAsync(
        Symbol symbol, CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetByStrategyIdAsync(
        Guid strategyId, bool includeClosedPositions = false, CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetClosedPositionsInRangeAsync(
        DateTime from, DateTime to, CancellationToken ct = default);
    Task AddAsync(Position position, CancellationToken ct = default);
    Task UpdateAsync(Position position, CancellationToken ct = default);
}

/// <summary>
/// 策略儲存庫介面
/// </summary>
public interface IStrategyRepository
{
    Task<Strategy?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Strategy?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Strategy>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Strategy>> GetByStatusAsync(
        StrategyStatus status, CancellationToken ct = default);
    Task AddAsync(Strategy strategy, CancellationToken ct = default);
    Task UpdateAsync(Strategy strategy, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// 交易所帳號儲存庫介面 — S24 起，金鑰由 SQLite 管理而非 appsettings.json。
/// SetActiveAsync 內部負責「同交易所至多一筆 active」的去活化動作。
/// </summary>
public interface IExchangeAccountRepository
{
    Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ExchangeAccount?> GetActiveAsync(ExchangeName exchange, CancellationToken ct = default);
    Task<IReadOnlyList<ExchangeAccount>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExchangeAccount>> GetByExchangeAsync(ExchangeName exchange, CancellationToken ct = default);
    Task AddAsync(ExchangeAccount account, CancellationToken ct = default);
    Task UpdateAsync(ExchangeAccount account, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// 把指定 id 設為該交易所唯一 active；同交易所其他帳號自動 Deactivate。
    /// 呼叫端負責 SaveChanges。
    /// </summary>
    Task SetActiveAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Unit of Work - 原子性地提交多個聚合的變更
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
