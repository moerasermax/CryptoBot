namespace CryptoBot.Application.Backtesting.Search;

/// <summary>
/// S69 — 自適應搜尋演算法的契約。與 <see cref="ISearchStrategy"/>「先列舉後並行跑」模型不同，
/// 自適應模式為「邊跑邊建議」：每跑完一個 trial 必須回報結果（<see cref="ReportResultAsync"/>），
/// sampler 才會根據歷史學習下一組推薦（<see cref="SuggestNextAsync"/>）。
///
/// 實作住在 <c>Infrastructure</c> 層（如 <c>BayesianSearchStrategy</c> 透過 HTTP 呼叫 Python sidecar），
/// 但介面留在 <c>Application</c>，符合 IRON §⑥ 相依方向「Application 不沾 Infrastructure」。
///
/// <see cref="IAsyncDisposable"/>：實作通常持有 sidecar study 句柄 / HttpClient / 連線狀態，
/// 優化結束後 Optimizer 會在 finally 區塊呼叫 DisposeAsync 做清理（如 DELETE study）。
/// </summary>
public interface IAdaptiveSearchStrategy : IAsyncDisposable
{
    /// <summary>
    /// 初始化 sampler — 通常會在 sidecar 端建立 study 並把參數空間註冊上去。
    /// 必須在第一次 <see cref="SuggestNextAsync"/> 之前呼叫一次。
    /// </summary>
    /// <param name="ranges">參數空間定義（與 ISearchStrategy 共用同樣的 <see cref="ParameterRange"/> 型別）</param>
    /// <param name="budget">本次 study 預計跑的 trial 上限 — 給 sampler 做超參數調整參考（部分演算法需要）</param>
    /// <param name="ct">取消權杖</param>
    Task InitializeAsync(IReadOnlyList<ParameterRange> ranges, int budget, CancellationToken ct);

    /// <summary>
    /// 取得下一組要嘗試的參數。serial 呼叫，前一輪 <see cref="ReportResultAsync"/> 回報結果後才能再次取建議
    /// （否則 sampler 沒新資訊可學）。
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> SuggestNextAsync(CancellationToken ct);

    /// <summary>
    /// 把上一輪建議參數實際跑出來的目標值（如 <see cref="BacktestReport.NetPnL"/>）回報給 sampler。
    /// 必須與最近一次 <see cref="SuggestNextAsync"/> 配對 — sampler 內部由 trial_id 串接。
    /// </summary>
    Task ReportResultAsync(IReadOnlyDictionary<string, decimal> parameters, decimal value, CancellationToken ct);
}
