using CryptoBot.Application.Ai;
using CryptoBot.ConsoleApp.Services;
using Microsoft.AspNetCore.Components;

namespace CryptoBot.ConsoleApp.Lab;

/// <summary>
/// 「策略大腦插槽」的合約：每個策略對應一個繼承這個的 Razor 元件，
/// 負責畫出自己的參數輸入框，並在被問到時把目前的輸入轉成 <see cref="OptimizationRequest"/>。
///
/// 元件之間透過 <see cref="ParameterChanged"/> EventCallback 把「目前格子數」回報給父頁面，
/// 父頁面就能顯示 grid size 預估，不必知道每個策略內部到底有幾個維度。
/// </summary>
public abstract class StrategyParameterFormBase : ComponentBase
{
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback<int> ParameterChanged { get; set; }

    /// <summary>表單目前的參數總組合數（笛卡兒積展開後的格數）。</summary>
    public abstract int CurrentGridSize { get; }

    /// <summary>
    /// 把目前表單狀態 + 時間窗 + UI 全局參數組成優化請求；驗證失敗時回傳 null 並設 <paramref name="error"/>。
    /// <paramref name="globals"/> 攜帶 Symbol / Interval / SlippageBps / InitialBalance — 由父頁面統一收集。
    /// </summary>
    public abstract OptimizationRequest? BuildRequest(
        OptimizationGlobals globals,
        DateTime startUtc,
        DateTime endUtc,
        out string? error);

    /// <summary>
    /// S25 T3：把「上次最佳結果」的整包參數灌回表單 — 將每個維度 Min/Max 都收窄到該值、Step=1，
    /// 用戶按「快速填入」後再按「開始掃描」即等於拿快取值回放一次，無需手動重建掃描區間。
    /// 子類別覆寫時只處理自己認得的 key — 未知 key 忽略，避免策略換型別後炸。
    /// 預設實作無動作，策略若暫不支援快取填入可保留預設。
    /// </summary>
    public virtual Task ApplyParametersAsync(IReadOnlyDictionary<string, decimal> parameters)
        => Task.CompletedTask;

    /// <summary>
    /// S30-GRID：AI 顧問建議的「網格參數」灌入表單 — 子類別把每個已知 key 的 <see cref="ParameterGridRange"/>
    /// 分別寫入 <c>_min / _max / _step</c>，讓「開始優化掃描」能直接跑 AI 規劃好的區間。
    /// 預設實作無動作，策略若暫不支援 AI 網格填入可保留預設。
    /// </summary>
    public virtual Task ApplyGridParametersAsync(IReadOnlyDictionary<string, ParameterGridRange> parameters)
        => Task.CompletedTask;

    /// <summary>
    /// S47：把表單目前的 Min/Max/Step 以 <see cref="ParameterGridRange"/> 字典形式回吐 —
    /// 讓 <c>LabStateContainer</c> 能快取使用者的網格設定，切頁後回填。
    /// Key 使用 <see cref="StrategyModel.ExpectedParameterKeys"/> 一致的 PascalCase 強型別鍵。
    /// 預設回空字典（策略若暫不實作持久化，LabStateContainer 會略過該 key 的快取寫入）。
    /// </summary>
    public virtual IReadOnlyDictionary<string, ParameterGridRange> CurrentGrid
        => new Dictionary<string, ParameterGridRange>();

    protected Task NotifyChangedAsync() => ParameterChanged.InvokeAsync(CurrentGridSize);

    /// <summary>
    /// S30-GRID-FIX：AI 偶爾會用 snake_case（<c>rsi_period</c>）或縮寫（<c>rsi</c>）取代我們的 PascalCase 強型別鍵。
    /// 這個 helper 讓子類別用一行處理：「以下任一別名命中就拿值」，全程 case-insensitive。
    /// 命中順序依 <paramref name="keyAliases"/> 傳入序——把「最正式的強型別鍵」放第一個。
    /// </summary>
    protected static bool TryResolve<T>(
        IReadOnlyDictionary<string, T> source,
        out T value,
        params string[] keyAliases)
    {
        if (source is not null && keyAliases is { Length: > 0 })
        {
            foreach (var alias in keyAliases)
            {
                foreach (var kv in source)
                {
                    if (string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        value = kv.Value;
                        return true;
                    }
                }
            }
        }
        value = default!;
        return false;
    }
}
