using System.Net.Http.Json;
using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Backtesting.Search;
using CryptoBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure.Backtesting.Search;

/// <summary>
/// S69 — 透過 Python FastAPI sidecar (Optuna TPE) 提供貝氏優化建議。
///
/// 生命週期：每次 optimize job 由 OptimizationOrchestrator 從 DI 取得新實例 (Transient)；
/// <see cref="InitializeAsync"/> 建立遠端 study 並把 study_id 留在實例狀態，
/// <see cref="DisposeAsync"/> 走 DELETE /study/{id} 清理 sidecar 端記憶體。
///
/// IRON 對齊：
/// - §⑥ 四層相依：本類住 Infrastructure，實作 Application 介面 <see cref="IAdaptiveSearchStrategy"/>。
/// - §⑨ 防腐層：JSON DTOs (Sidecar*) 為 internal，不滲透至 Application/Domain。Suggest 結果在類內
///   完成 double → decimal 翻譯後才回傳上層。
/// - §① 精度絕對論：sidecar 回傳 double，本類負責收斂為 decimal 給 Optimizer 使用 — 雖然 Optuna 端
///   會把 step 拉成合法值，但 .NET 端仍以 decimal 為單一真相。
///
/// trial_id 配對：Optuna ask/tell 用 trial_id 串接，但這是 sidecar internal 概念，不該洩漏到
/// Optimizer.RunAsync 看到的 paramSet 字典。改用「pending trial」單格內部狀態 — Suggest 寫入、
/// Report 讀取後清空，違反 sequential 配對協議時直接拋例外。
/// </summary>
public sealed class BayesianSearchStrategy : IAdaptiveSearchStrategy
{
    private readonly HttpClient _http;
    private readonly ILogger<BayesianSearchStrategy> _logger;
    private string? _studyId;
    private int? _pendingTrialId;
    private bool _disposed;

    public BayesianSearchStrategy(
        HttpClient http,
        IOptions<BayesianSidecarOptions> options,
        ILogger<BayesianSearchStrategy> logger)
    {
        _http = http;
        _logger = logger;

        var opts = options.Value;
        // BaseAddress 必須帶 trailing slash 否則 PostAsync 相對 URL 拼接會丟掉 path 段。
        _http.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
    }

    public async Task InitializeAsync(IReadOnlyList<ParameterRange> ranges, int budget, CancellationToken ct)
    {
        if (_studyId is not null)
            throw new InvalidOperationException("BayesianSearchStrategy already initialized.");

        var specs = ranges
            .Select(r => new SidecarParameterSpec(r.Name, (double)r.Min, (double)r.Max, (double)r.Step))
            .ToList();

        var req = new SidecarStudyCreateRequest("maximize", specs);
        using var resp = await _http.PostAsJsonAsync("study/create", req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SidecarStudyCreateResponse>(cancellationToken: ct)
                       .ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Sidecar study/create returned null body.");

        _studyId = body.StudyId;
        _logger.LogInformation(
            "🧠 [BAYESIAN] Sidecar study created — id={StudyId} dimensions={Count} budget={Budget}",
            _studyId, ranges.Count, budget);
    }

    public async Task<IReadOnlyDictionary<string, decimal>> SuggestNextAsync(CancellationToken ct)
    {
        EnsureInitialized();
        if (_pendingTrialId is not null)
            throw new InvalidOperationException(
                "Previous trial not yet reported; ReportResultAsync must be called between Suggest calls.");

        using var resp = await _http.PostAsync($"study/{_studyId}/suggest", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SidecarSuggestResponse>(cancellationToken: ct)
                       .ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Sidecar suggest returned null body.");

        _pendingTrialId = body.TrialId;
        // double → decimal 翻譯（IRON §①）；參數值離開本類後一律 decimal。
        return body.Parameters.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value);
    }

    public async Task ReportResultAsync(
        IReadOnlyDictionary<string, decimal> parameters,
        decimal value,
        CancellationToken ct)
    {
        // 介面為了通用性收 parameters，本實作用內部 _pendingTrialId 配對，故 parameters 暫不使用。
        _ = parameters;

        EnsureInitialized();
        if (_pendingTrialId is null)
            throw new InvalidOperationException("No pending trial to report; SuggestNextAsync must be called first.");

        var req = new SidecarTellRequest(_pendingTrialId.Value, (double)value);
        using var resp = await _http.PostAsJsonAsync($"study/{_studyId}/tell", req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        _pendingTrialId = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_studyId is null) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await _http.DeleteAsync($"study/{_studyId}", cts.Token).ConfigureAwait(false);
            // 失敗也不拋 — 此時 optimize 已完成，sidecar 清理失敗只是記憶體遺留，重啟自然回收。
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning(
                    "🧠 [BAYESIAN] Sidecar study delete returned {Status}; leaving residual study {StudyId}.",
                    (int)resp.StatusCode, _studyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🧠 [BAYESIAN] Sidecar study delete failed for {StudyId}; ignoring.", _studyId);
        }
    }

    private void EnsureInitialized()
    {
        if (_studyId is null)
            throw new InvalidOperationException("BayesianSearchStrategy not initialized; call InitializeAsync first.");
        if (_disposed)
            throw new ObjectDisposedException(nameof(BayesianSearchStrategy));
    }
}
