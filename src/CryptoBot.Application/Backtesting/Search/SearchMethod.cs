namespace CryptoBot.Application.Backtesting.Search;

/// <summary>
/// UI 與 API 共享的搜尋演算法選項。Orchestrator 收到 <see cref="OptimizationRequest"/> 後
/// 會依此 enum 派發到具體的 <see cref="ISearchStrategy"/> 實例 — Optimizer 本身不識別 enum，
/// 永遠只看抽象 interface。
/// </summary>
public enum SearchMethod
{
    /// <summary>
    /// 笛卡兒積全網格 — 行為等同重構前 <see cref="StrategyOptimizer"/> 內嵌的展開邏輯。
    /// 預設值（=0）讓未升級的舊呼叫端 / JSON payload 自動落到此模式，維持向後相容。
    /// </summary>
    Grid = 0,

    /// <summary>
    /// 隨機抽樣 — 各維度獨立均勻取樣，掃描次數由 <c>RandomBudget</c> 控制。
    /// 適用於高維度空間（笛卡兒積過大）或前期粗掃。
    /// </summary>
    Random = 1,
}
