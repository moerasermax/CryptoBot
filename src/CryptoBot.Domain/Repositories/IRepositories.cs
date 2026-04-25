using CryptoBot.Domain.Aggregates.AiCredentialAggregate;
using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Aggregates.StrategyOptimizationAggregate;
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

    /// <summary>
    /// S66-A：依本地決定性產生的 <see cref="Order.ClientOrderId"/> 取單。
    /// 用於 DiagnosticTool <c>s66a_check-order</c> 與冪等性對帳。
    /// </summary>
    Task<Order?> GetByClientOrderIdAsync(string clientOrderId, CancellationToken ct = default);

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
    /// <summary>S39：Dashboard 歷史面板用 — 依 ClosedAt 由新到舊取前 N 筆已平倉紀錄。</summary>
    Task<IReadOnlyList<Position>> GetRecentClosedAsync(int limit, CancellationToken ct = default);
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
/// 策略最佳化設定儲存庫介面 — S22 引入。
///
/// 識別語意：複合鍵 (StrategyId, Symbol, KlineInterval) 每組唯一一筆。
/// <see cref="StrategyOptimizationSettings"/> 本身不是 AggregateRoot，
/// 因此本介面負責把「查找 + 寫入」語意封裝成 Upsert 行為，呼叫端不用關心
/// 「這組 key 之前存不存在」。
///
/// 呼叫端負責 <see cref="IUnitOfWork.SaveChangesAsync"/>。
/// </summary>
public interface IStrategyOptimizationSettingsRepository
{
    /// <summary>
    /// 依複合鍵取得單筆設定；查無則回傳 null。
    /// Symbol 以 <see cref="Symbol.BingXFormat"/> 內部存放，呼叫端傳 Value Object 即可。
    /// </summary>
    Task<StrategyOptimizationSettings?> GetAsync(
        Guid strategyId, Symbol symbol, KlineInterval interval, CancellationToken ct = default);

    /// <summary>
    /// 取得指定策略的所有最佳化快照（跨 Symbol / Interval），通常供 Lab 頁一次列表顯示。
    /// </summary>
    Task<IReadOnlyList<StrategyOptimizationSettings>> GetByStrategyAsync(
        Guid strategyId, CancellationToken ct = default);

    /// <summary>
    /// Upsert：同鍵存在則覆寫 ParametersJson / Score / UpdatedAt；不存在則新增。
    /// 呼叫端必須先用 <see cref="StrategyOptimizationSettings.Create"/> 建好實例，
    /// UpdatedAt 由呼叫端填（Domain 層不依賴系統時鐘）。
    /// </summary>
    Task UpsertAsync(StrategyOptimizationSettings settings, CancellationToken ct = default);
}

/// <summary>
/// AI 服務金鑰儲存庫 — S30 引入。
/// 每個 provider 至多一筆：Upsert 語意由 Repository 實作負責。
/// </summary>
public interface IAiCredentialRepository
{
    Task<AiCredential?> GetByProviderAsync(string provider, CancellationToken ct = default);
    Task UpsertAsync(AiCredential credential, CancellationToken ct = default);
    Task DeleteByProviderAsync(string provider, CancellationToken ct = default);
}

/// <summary>
/// Unit of Work - 原子性地提交多個聚合的變更
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// S53 T1：樂觀併發重試版 SaveChanges。遇到 <c>DbUpdateConcurrencyException</c>
    /// 時會針對每個衝突 entry 從 DB 重抓 values，套用 <i>client-wins</i> 策略
    /// （OriginalValues 對齊 DB 當前值、CurrentValues 保留 client 的變更），
    /// 再重試最多 <paramref name="maxAttempts"/> 次。若 entry 對應的 row 已不存在，
    /// 則把 state 標為 Detached — 視為「已被處理」，不再嘗試寫回。
    ///
    /// <para>
    /// 使用時機：Strategies / Trading 的 <c>HandleSignalAsync</c> 與 AccountSynchronizer 的
    /// Order / Position 寫入可能交錯（WS 事件 vs 策略迴圈）；用這個版本避免 `SIGNAL BUY`
    /// 成交後因併發衝突而無紀錄落地的情況。
    /// </para>
    /// </summary>
    Task<int> SaveChangesWithRetryAsync(int maxAttempts = 3, CancellationToken ct = default);

    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
