using System.Collections.Concurrent;
using CryptoBot.Application.Ai;
using CryptoBot.Application.Common;
using CryptoBot.ConsoleApp.Realtime;
using CryptoBot.ConsoleApp.Services;
using CryptoBot.Domain.Enums;

namespace CryptoBot.ConsoleApp.Lab;

/// <summary>
/// S47-REVISED：整個 Lab 頁面全域表單的「鋼鐵級快照」—
/// Symbol / Interval / Slippage / Initial / Leverage / Window 全部入袋，
/// 切到 Dashboard 再回 /lab 時原樣恢復。
///
/// <para>
/// 為什麼不只存 Grid：PM 驗收條件是「所有網格參數必須完好如初」——
/// 使用者心裡「網格」含全表單（從 Market 到 Window 到槓桿），不只是策略 Min/Max/Step。
/// </para>
/// </summary>
public sealed record LabFormSnapshot(
    string SymbolInput,
    string SymbolSelect,
    bool IsManualSymbol,
    KlineInterval Interval,
    decimal SlippageBps,
    decimal InitialBalance,
    int Leverage,
    DateTime StartDateUtc,
    DateTime EndDateUtc);

/// <summary>
/// 整個 Lab 介面的「狀態艙」— Singleton，跨頁面、跨重整、跨 tab 切換都不丟資料。
///
/// 雙線訂閱：
/// - <see cref="DashboardEventBus"/>：本 process 內的 progress / completed / failed 事件直接灌進來
/// - <see cref="ScheduleEtaTick"/>：每秒重算 ETA 推一次 <see cref="StateChanged"/>，UI 計時器才會跑
///
/// 多人併發注意：本系統定位是單一老闆面板（自架 BingX demo bot），所以單例是合理的。
/// 真要多用戶，就把這個改成 Scoped + 用 Blazor circuit context 隔離。
/// </summary>
public sealed class LabStateContainer : IDisposable
{
    private readonly DashboardEventBus _bus;
    private readonly StrategyCatalog _catalog;
    private readonly IEnvironmentSwitcher _envSwitcher;
    private readonly object _lock = new();
    private DateTime? _runStartedUtc;
    private Timer? _etaTimer;

    // S47：策略 key → 最後一次的網格 Min/Max/Step 快照。Container 是 Singleton，
    // 使用者切頁到 Dashboard 再回來時，BacktestLab 能從這裡拿回自己上次的設定。
    // ConcurrentDictionary 因為 BacktestLab 可能在不同 circuit 被並行存取（多分頁）。
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ParameterGridRange>> _gridCache = new();

    // S47-REVISED：全域表單快照 — Symbol / Interval / Slippage / Initial / Leverage / Window。
    // 單一 singleton 對單一老闆面板，不用 keyed 結構；null = 從未保存過（用 UI 預設值）。
    private LabFormSnapshot? _formSnapshot;

    public LabStateContainer(
        DashboardEventBus bus,
        StrategyCatalog catalog,
        IEnvironmentSwitcher envSwitcher)
    {
        _bus = bus;
        _catalog = catalog;
        _envSwitcher = envSwitcher;
        SelectedModel = _catalog.Default;
        CurrentMode = _envSwitcher.CurrentMode;

        _bus.OptimizationProgress  += OnProgress;
        _bus.OptimizationCompleted += OnCompleted;
        _bus.OptimizationFailed    += OnFailed;

        _envSwitcher.EnvironmentChanged += OnEnvironmentChanged;
    }

    // ── public state ──
    public StrategyModel SelectedModel { get; private set; }
    public OptimizationProgressUpdate? Progress { get; private set; }
    public OptimizationCompletedUpdate? Leaderboard { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => Progress is not null && Leaderboard is null && LastError is null;
    public TimeSpan? Elapsed => _runStartedUtc is null ? null : DateTime.UtcNow - _runStartedUtc;

    /// <summary>當前生效的環境模式。SwitchAsync 後會被 EnvironmentChanged 同步更新。</summary>
    public TradingMode CurrentMode { get; private set; }

    /// <summary>最近一次切換的摘要（含被強制停下的策略數量），UI 用來顯示提示。</summary>
    public EnvironmentChangedEvent? LastEnvChange { get; private set; }

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            var p = Progress;
            if (p is null || p.Completed == 0 || p.Total == 0 || _runStartedUtc is null) return null;
            var elapsed = DateTime.UtcNow - _runStartedUtc.Value;
            var perItem = elapsed.TotalSeconds / p.Completed;
            var remaining = perItem * (p.Total - p.Completed);
            return TimeSpan.FromSeconds(Math.Max(remaining, 0));
        }
    }

    public event Action? StateChanged;

    // ── S47 grid settings persistence ──

    /// <summary>
    /// S47：儲存某策略 key 目前的 Min/Max/Step 網格設定。空字典會被忽略，避免表單初始化
    /// 尚未回報時把有用的舊快取洗掉。
    /// </summary>
    public void SaveGridSettings(string strategyKey, IReadOnlyDictionary<string, ParameterGridRange> grid)
    {
        if (string.IsNullOrWhiteSpace(strategyKey)) return;
        if (grid is null || grid.Count == 0) return;
        _gridCache[strategyKey] = grid;
    }

    /// <summary>
    /// S47：取某策略 key 的快取網格；沒有快取時回 <c>null</c>，讓 UI 維持表單預設值。
    /// </summary>
    public IReadOnlyDictionary<string, ParameterGridRange>? TryGetGridSettings(string strategyKey)
        => _gridCache.TryGetValue(strategyKey, out var v) ? v : null;

    /// <summary>
    /// S47-REVISED：保存全域表單快照 — BacktestLab 任何 <c>@bind:after</c> 觸發時呼叫，
    /// 讓切頁 / refresh 都不丟 Symbol / Interval / Slippage / Initial / Leverage / Window。
    /// </summary>
    public void SaveFormSnapshot(LabFormSnapshot snapshot)
    {
        if (snapshot is null) return;
        lock (_lock) _formSnapshot = snapshot;
    }

    /// <summary>
    /// S47-REVISED：取全域表單快照；沒快照時回 <c>null</c>，由 UI 用自身預設值。
    /// </summary>
    public LabFormSnapshot? TryGetFormSnapshot()
    {
        lock (_lock) return _formSnapshot;
    }

    // ── mutations ──
    public void SelectModel(string key)
    {
        var model = _catalog.FindByKey(key);
        if (model is null || model.IsLocked) return;
        lock (_lock) SelectedModel = model;
        Notify();
    }

    /// <summary>
    /// UI 觸發的環境切換入口 — 委派給 <see cref="IEnvironmentSwitcher"/>，
    /// 切完事件由 <see cref="OnEnvironmentChanged"/> 回流到此 container 並 Notify UI。
    ///
    /// Demo→Live 的二次確認在 UI 層做（<c>EnvironmentSwitchModal</c>），這裡不再防呆，
    /// 因為 API / 自動化也要能直呼這個方法做切換。
    /// </summary>
    public Task<EnvironmentSwitchResult> ChangeEnvironmentAsync(TradingMode newMode, string? reason = null, CancellationToken ct = default)
        => _envSwitcher.SwitchAsync(newMode, reason, ct);

    private void OnEnvironmentChanged(EnvironmentChangedEvent evt)
    {
        lock (_lock)
        {
            CurrentMode = evt.ToMode;
            LastEnvChange = evt;
        }
        Notify();
    }

    /// <summary>
    /// 進度條開始前由 BacktestLab 呼叫 — 清掉舊結果、開計時器。
    /// </summary>
    public void NotifyJobStarting(int totalCombinations)
    {
        lock (_lock)
        {
            _runStartedUtc = DateTime.UtcNow;
            Progress = new OptimizationProgressUpdate(0, totalCombinations, "queued…");
            Leaderboard = null;
            LastError = null;
            _etaTimer ??= new Timer(_ => Notify(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        Notify();
    }

    private void OnProgress(OptimizationProgressUpdate u)
    {
        lock (_lock)
        {
            // 第一筆進度且使用者沒有先 NotifyJobStarting（例如外部觸發），補上開始時間
            _runStartedUtc ??= DateTime.UtcNow;
            Progress = u;
        }
        Notify();
    }

    private void OnCompleted(OptimizationCompletedUpdate u)
    {
        lock (_lock)
        {
            Leaderboard = u;
            StopEtaTimer();
        }
        Notify();
    }

    private void OnFailed(OptimizationFailedUpdate u)
    {
        lock (_lock)
        {
            LastError = u.Error;
            StopEtaTimer();
        }
        Notify();
    }

    private void StopEtaTimer()
    {
        _etaTimer?.Dispose();
        _etaTimer = null;
    }

    private void Notify() => StateChanged?.Invoke();

    public void Dispose()
    {
        _bus.OptimizationProgress  -= OnProgress;
        _bus.OptimizationCompleted -= OnCompleted;
        _bus.OptimizationFailed    -= OnFailed;
        _envSwitcher.EnvironmentChanged -= OnEnvironmentChanged;
        StopEtaTimer();
    }
}
