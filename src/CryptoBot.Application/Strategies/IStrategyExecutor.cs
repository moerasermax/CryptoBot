using CryptoBot.Domain.Aggregates.StrategyAggregate;

namespace CryptoBot.Application.Strategies;

/// <summary>
/// 單一策略的執行器 —
/// 負責接收市場資料、組裝上下文、呼叫 <see cref="IStrategy.AnalyzeAsync"/>、
/// 經風控後下單，並處理錯誤與自我停機。
///
/// 生命週期：每個 <see cref="Strategy"/> entity 搭配一個 executor 實例，
/// 由 <see cref="IStrategyExecutorFactory"/> 建立。
/// </summary>
public interface IStrategyExecutor : IAsyncDisposable
{
    /// <summary>對應的 Strategy aggregate 的 Id。</summary>
    Guid StrategyId { get; }

    /// <summary>是否正在執行中（已 StartAsync 且未 StopAsync）。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// S42：上一次完成 <see cref="IStrategy.AnalyzeAsync"/> 的 UTC 時間；未執行過則為 <c>null</c>。
    /// Dashboard 心跳標籤靠這個屬性 + WS 推播雙管齊下（REST 拿初值、WS 拿後續跳動）。
    /// </summary>
    DateTime? LastEvaluatedAtUtc { get; }

    /// <summary>
    /// 啟動策略：預載歷史 K 線、訂閱即時 K 線更新，開始分析迴圈。
    /// 同一實例重複呼叫為冪等（已啟動則直接回傳）。
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止策略：取消訂閱、等待進行中的分析完成、釋放資源。
    /// 停機後 <see cref="IsRunning"/> 恆為 false。
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// 策略執行器工廠 —
/// 為每個 <see cref="Strategy"/> + <see cref="IStrategy"/> 組合建立對應的 Executor 實例。
/// 由 DI 註冊為 Scoped 或 Singleton（本專案用 Singleton：建立的 Executor 會自己管理 scope）。
/// </summary>
public interface IStrategyExecutorFactory
{
    IStrategyExecutor Create(Strategy strategy, IStrategy strategyImpl);
}
