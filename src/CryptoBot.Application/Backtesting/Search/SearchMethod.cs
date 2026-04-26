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

    /// <summary>
    /// 貝氏優化 — 透過 Optuna sidecar TPE sampler「邊跑邊建議」，下一組參數依過往 trial 結果學習推薦。
    /// 走 <see cref="IAdaptiveSearchStrategy"/> 路徑（與 Grid/Random 的 <see cref="ISearchStrategy"/> 不同），
    /// 優化執行為序列模式而非並行。掃描次數同樣由 <c>RandomBudget</c> 欄位控制（與 Random 共用 budget 語意）。
    /// </summary>
    Bayesian = 2,
}
