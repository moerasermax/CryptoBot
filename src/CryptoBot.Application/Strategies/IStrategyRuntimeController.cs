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

    /// <summary>
    /// S25：把指定策略的「決策大腦」熱換成另一個類型字串（必須已註冊於 <c>IStrategyFactory</c>）。
    /// 契約：
    ///  - 若策略正在跑 → 先停 executor → 翻 DB → 重新掛載 executor → 該策略 Status 維持 Running
    ///  - 若策略已停 → 只翻 DB Type，不啟動
    ///  - 新類型字串未註冊 → 回 false（DB 不變、executor 狀態不變）
    /// 回傳：切換是否成功。
    /// </summary>
    Task<bool> ChangeStrategyTypeAsync(Guid strategyId, string newStrategyType, CancellationToken ct = default);
}
