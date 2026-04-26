using CryptoBot.Application.Ai;
using CryptoBot.ConsoleApp.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// S55 T1：由父頁面（BacktestLab）在 <c>_formParameters</c> 傳入的策略 key — 用來從
    /// <see cref="LabStateContainer"/> 精準索回屬於自己的網格快照。子類別在 OnInitializedAsync
    /// 內呼叫 <see cref="RestoreGridFromCacheAsync"/> 時會用到；未綁定（空字串）時 helper 靜默跳過。
    /// </summary>
    [Parameter] public string StrategyKey { get; set; } = default!;

    /// <summary>
    /// S55 T1：領取式持久化的狀態艙 — 注入（而非由父頁面透過 DynamicComponent 傳），
    /// 因為 LabStateContainer 是 Singleton，直接 DI 拿就好，不用繞一圈 Parameter。
    /// </summary>
    [Inject] protected LabStateContainer State { get; set; } = default!;

    /// <summary>S55 T4：診斷 Log — 表單自領取快取時印出恢復紀錄。</summary>
    [Inject] protected ILogger<StrategyParameterFormBase> Logger { get; set; } = default!;

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
    /// S55 T1/T2：領取式持久化的核心 helper — 子類別的 <see cref="OnInitializedAsync"/> 第一件事
    /// 就呼叫這個，讓表單在第一次渲染前就已經是快取值，而非預設值中間態。
    ///
    /// 回傳值語意：
    /// <list type="bullet">
    ///   <item><c>true</c>：快取命中且已套用 — 子類別可跳過 <see cref="NotifyChangedAsync"/>，
    ///     因為 <see cref="ApplyGridParametersAsync"/> 內部已經發過一次。</item>
    ///   <item><c>false</c>：無快取（首次進頁 / 該 key 沒記錄）— 子類別仍需 <see cref="NotifyChangedAsync"/>
    ///     把預設 grid size 回報給父頁面，否則 Grid size 欄位會顯示 "—"。</item>
    /// </list>
    /// </summary>
    protected async Task<bool> RestoreGridFromCacheAsync()
    {
        if (string.IsNullOrWhiteSpace(StrategyKey))
        {
            Logger?.LogDebug(
                "StrategyParameterForm: StrategyKey 未綁定（父頁面沒傳進來），跳過快取恢復，走預設值。");
            return false;
        }

        var cached = State?.TryGetGridSettings(StrategyKey);
        if (cached is null || cached.Count == 0)
        {
            Logger?.LogDebug(
                "StrategyParameterForm [{Key}]：LabStateContainer 內無網格快取，維持表單預設值。",
                StrategyKey);
            return false;
        }

        Logger?.LogDebug(
            "StrategyParameterForm [{Key}]：自領取快取網格成功，覆寫 {Count} 個維度 → {Keys}。",
            StrategyKey, cached.Count, string.Join(",", cached.Keys));
        await ApplyGridParametersAsync(cached);
        return true;
    }

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
