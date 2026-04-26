namespace CryptoBot.Application.Backtesting.Search;

/// <summary>
/// 參數搜尋演算法的契約 — 給定參數範圍清單，列舉本次優化要嘗試的參數組合。
/// 不負責並行、不負責執行回測；單純把「該掃哪些點」與 <see cref="StrategyOptimizer"/>
/// 的執行迴圈解耦，將來新增 Bayesian / Genetic / TPE 等演算法只要 implement 這支介面、
/// Optimizer 與 Orchestrator 的執行骨架不必動。
/// </summary>
public interface ISearchStrategy
{
    /// <summary>
    /// 列舉本次要評估的參數組合。每個字典代表一組 (name → value) 的參數設定。
    /// 實作應在合理範圍內 deterministic（給定相同 ranges + 種子）— 否則回放與測試不可重現。
    /// 由 <see cref="StrategyOptimizer.RunAsync"/> 在主執行緒一次性物化為 list 後分派並行，
    /// 因此回傳的 <see cref="System.Collections.Generic.IEnumerable{T}"/> 不需要 thread-safe。
    /// </summary>
    IEnumerable<IReadOnlyDictionary<string, decimal>> Enumerate(
        IReadOnlyList<ParameterRange> ranges);
}
