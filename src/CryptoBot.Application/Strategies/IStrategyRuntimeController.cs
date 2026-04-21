namespace CryptoBot.Application.Strategies;

/// <summary>
/// 允許 API 端對正在執行的策略做熱啟停（不需要重啟整個 host）。
///
/// 實作由 <see cref="StrategyRuntimeHostedService"/> 提供 — 它本來就管著所有 executor 的
/// 生命週期，多曝露兩個方法給 API 用就好，避免 Web 那層自己另外維護一份 executor 狀態。
///
/// 契約：
/// - <see cref="StartAsync(Guid, CancellationToken)"/> 把 DB 裡該策略狀態設為 Running 並 new Executor
///   啟動；若已在跑就直接回 true（冪等）。
/// - <see cref="StopAsync(Guid, CancellationToken)"/> 反向操作；策略不存在或未在跑回 false。
/// </summary>
public interface IStrategyRuntimeController
{
    Task<bool> StartAsync(Guid strategyId, CancellationToken ct = default);
    Task<bool> StopAsync(Guid strategyId, CancellationToken ct = default);
    bool IsRunning(Guid strategyId);

    /// <summary>
    /// 一次停掉所有正在跑的 executor — 環境切換 / 緊急熔斷情境用。
    /// 回傳實際停下來的 strategyId 清單；DB 狀態同步翻為 Stopped 並寫入給定原因。
    /// </summary>
    Task<IReadOnlyList<Guid>> StopAllAsync(string reason, CancellationToken ct = default);

    /// <summary>當下所有掛載中的策略 id 快照（含 IsRunning=true 的）。</summary>
    IReadOnlyList<Guid> RunningStrategyIds { get; }
}
