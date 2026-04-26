using System.Text.Json;
using CryptoBot.Application.Backtesting.Search;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.Strategies;
using CryptoBot.ConsoleApp.Lab;
using CryptoBot.ConsoleApp.Services;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/lab/*</c> — 回測實驗室的 Minimal API。
///
/// <list type="bullet">
///   <item><c>POST /api/lab/optimize</c> — 啟動參數優化（非同步；立即回 202 + <c>job</c> 狀態）</item>
///   <item><c>POST /api/lab/apply/{strategyId}</c> — 把排行榜某一行的整包參數熱套用到現有策略</item>
///   <item><c>GET  /api/lab/status</c> — 查「目前是否有任務在跑」— 頁面重整時用</item>
/// </list>
/// </summary>
public static class LabEndpoints
{
    public static IEndpointRouteBuilder MapLabEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lab").WithTags("Lab");

        group.MapGet("/status", (OptimizationOrchestrator orchestrator) =>
            Results.Ok(new { isRunning = orchestrator.IsRunning }));

        // S69 Phase 3：Sidecar 在線探針 — UI 切到 Bayesian 時 reactive 探一次，offline 即顯示「AI 引擎離線」。
        // 故意短 timeout（2s）+ 包覆所有例外為「online=false」回應，避免 UI 端拉長等待或撞 500。
        group.MapGet("/sidecar/health", async (
            IOptions<BayesianSidecarOptions> opts,
            CancellationToken ct) =>
        {
            var cfg = opts.Value;
            using var http = new HttpClient
            {
                BaseAddress = new Uri(cfg.BaseUrl.EndsWith('/') ? cfg.BaseUrl : cfg.BaseUrl + "/"),
                Timeout = TimeSpan.FromSeconds(2),
            };
            try
            {
                using var resp = await http.GetAsync("healthz", ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    online = resp.IsSuccessStatusCode,
                    statusCode = (int)resp.StatusCode,
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { online = false, error = ex.Message });
            }
        });

        // S25 T1：查詢上次優化存檔。{strategyKey} 為 StrategyCatalog 的 key（例 "trend"），
        // 搭配 query symbol+interval 組成唯一鍵。沒有快取時回 404。
        group.MapGet("/cached-settings/{strategyKey}", async (
            string strategyKey,
            string symbol,
            KlineInterval interval,
            IStrategyOptimizationSettingsRepository repo,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(strategyKey))
                return Results.BadRequest(new { error = "strategyKey is required." });
            if (string.IsNullOrWhiteSpace(symbol))
                return Results.BadRequest(new { error = "symbol query parameter is required." });

            Symbol symbolVo;
            try { symbolVo = Symbol.Parse(symbol); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }

            var strategyGuid = LabStrategyKey.ToGuid(strategyKey);
            var settings = await repo.GetAsync(strategyGuid, symbolVo, interval, ct).ConfigureAwait(false);
            if (settings is null)
                return Results.NotFound(new { error = "No cached settings for this combination." });

            IReadOnlyDictionary<string, decimal> parameters;
            try
            {
                parameters = JsonSerializer.Deserialize<Dictionary<string, decimal>>(settings.ParametersJson)
                             ?? new Dictionary<string, decimal>();
            }
            catch (JsonException)
            {
                // 舊資料格式不相容 — 視為沒有快取，不回 500 污染 UI。
                return Results.NotFound(new { error = "Cached settings payload is not JSON-decodable." });
            }

            return Results.Ok(new CachedSettingsDto(
                StrategyKey: strategyKey,
                Symbol: settings.Symbol,
                Interval: settings.Interval,
                Parameters: parameters,
                Score: settings.Score,
                UpdatedAtUtc: settings.UpdatedAt));
        });

        group.MapPost("/optimize", (OptimizationRequest req, OptimizationOrchestrator orchestrator) =>
        {
            if (!ValidateRequest(req, out var error))
                return Results.BadRequest(new { error });

            var started = orchestrator.TryStart(req);
            return started
                ? Results.Accepted("/api/lab/status", new { accepted = true })
                : Results.Conflict(new { error = "An optimization job is already running." });
        });

        group.MapPost("/apply/{strategyId:guid}", async (
            Guid strategyId,
            ApplyParamsRequest body,
            IStrategyRepository repo,
            IStrategyRuntimeController controller,
            StrategyCatalog catalog,
            IRealtimeBroadcaster broadcaster,
            IUnitOfWork uow,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LabEndpoints.Apply");

            if (body.Parameters is null || body.Parameters.Count == 0)
                return Results.BadRequest(new { error = "Parameters must not be empty." });

            // S45：StrategyKey（實驗室選的模型）→ 目標 Domain StrategyType + Catalog DisplayName。
            // 沒帶 key 時走 S25 舊行為（只套參數、不改型、不改名）。
            // S52 T1：`targetModel` 向外提升作用域，跨模型套用時用它的 ExpectedParameterKeys 過濾。
            StrategyModel? targetModel = null;
            string? targetStrategyType = null;
            string? modelDisplayName = null;
            if (!string.IsNullOrWhiteSpace(body.StrategyKey))
            {
                targetModel = catalog.FindByKey(body.StrategyKey);
                if (targetModel is null)
                    return Results.BadRequest(new { error = $"Unknown StrategyKey '{body.StrategyKey}'." });
                targetStrategyType = MapLabKeyToStrategyType(body.StrategyKey);
                if (targetStrategyType is null)
                    return Results.BadRequest(new { error = $"StrategyKey '{body.StrategyKey}' has no Domain StrategyType mapping." });
                modelDisplayName = targetModel.DisplayName;
            }

            var strategy = await repo.GetByIdAsync(strategyId, ct).ConfigureAwait(false);
            if (strategy is null)
                return Results.NotFound(new { error = $"Strategy {strategyId} not found." });

            // S52 T3：把主要流程包在 try/catch — 換型 / 參數映射 / Domain 檢查任何一步
            // 出 DomainException 或已知映射錯誤都回 400（帶明確訊息），不讓 Web API 噴 500 崩潰。
            try
            {
                // 熱套用：Stop → (ChangeType + Rename + UpdateConfiguration) → Save → Start。
                // 單一 Stop/Start 循環內做完三件事，background executor 重建時就能載到新大腦 + 新名字。
                var wasRunning = strategy.Status == StrategyStatus.Running;
                if (wasRunning)
                {
                    await controller.StopAsync(strategyId, ct).ConfigureAwait(false);
                    // reload — StopAsync 已自行寫一次 DB 把 Status 翻為 Stopped
                    strategy = await repo.GetByIdAsync(strategyId, ct).ConfigureAwait(false)
                               ?? throw new InvalidOperationException("Strategy vanished after stop.");
                }

                // S52 T1：跨模型套用偵測。當 UI 選的目標模型 (targetStrategyType) 與目前卡片的
                // StrategyType 不同（例如 PriceAction → MeanReversion），舊模型的參數必須被「整條洗掉」，
                // 只留新模型的 ExpectedParameterKeys 裡存在的鍵。否則 `WickToBodyRatio`（PA 獨有）
                // 會跟 `BbPeriod`（Bollinger 獨有）混在同一份 newConfig.Parameters，
                // Strategy.ChangeType 後新腦讀到殘留參數直接錯算，嚴重時 500 崩潰。
                var isCrossModelMorph = targetStrategyType is not null
                                        && !string.Equals(strategy.StrategyType, targetStrategyType, StringComparison.Ordinal);

                Dictionary<string, decimal> newParams;
                if (isCrossModelMorph && targetModel is not null)
                {
                    // 跨模型：空字典起手，只揀目標模型聲明過的鍵（且本次優化有帶值）。
                    // 目標模型聲明但本次優化沒帶的鍵 → 留空，由策略自身 `GetParameter(key, default)` fallback 提供。
                    newParams = new Dictionary<string, decimal>(targetModel.ExpectedParameterKeys.Count);
                    foreach (var key in targetModel.ExpectedParameterKeys)
                    {
                        if (body.Parameters.TryGetValue(key, out var v))
                            newParams[key] = v;
                    }
                    logger.LogInformation(
                        "Cross-model apply for strategy {Id}: {From} → {To}; kept {Kept}/{Expected} keys, dropped {Dropped} residual keys from old model.",
                        strategyId, strategy.StrategyType, targetStrategyType,
                        newParams.Count, targetModel.ExpectedParameterKeys.Count,
                        strategy.Configuration.Parameters.Count);
                }
                else
                {
                    // 同模型：保留原字典中未被本次覆寫的鍵（S25 既有行為）。
                    newParams = new Dictionary<string, decimal>(strategy.Configuration.Parameters);
                    foreach (var kv in body.Parameters)
                        newParams[kv.Key] = kv.Value;
                }

                var current = strategy.Configuration;

                // S42-S47 T2：套用時可同步覆寫 Symbol / Interval，避免「Lab 在 SOL-15m 優化完，
                // 卻把結果硬套到還停留在 BTC-1h 的卡片上」這種資料錯位。
                // 未帶時維持 current；帶了就解析成新值並在 newConfig 與 BuildOptimizedName 都用。
                Symbol targetSymbol;
                if (!string.IsNullOrWhiteSpace(body.Symbol))
                {
                    try { targetSymbol = Symbol.Parse(body.Symbol); }
                    catch (DomainException ex) { return Results.BadRequest(new { error = $"Invalid Symbol '{body.Symbol}': {ex.Message}" }); }
                }
                else
                {
                    targetSymbol = current.Symbol;
                }
                var targetInterval = body.Interval ?? current.Interval;

                var newConfig = StrategyConfiguration.Create(
                    symbol: targetSymbol,
                    interval: targetInterval,
                    leverage: current.Leverage,
                    riskPerTradePercent: current.RiskPerTradePercent,
                    stopLossPercent: current.StopLossPercent,
                    takeProfitPercent: current.TakeProfitPercent,
                    trailingStopPercent: current.TrailingStopPercent,
                    maxConcurrentPositions: current.MaxConcurrentPositions,
                    cooldownPeriod: current.CooldownPeriod,
                    maxKlineWindow: current.MaxKlineWindow,
                    parameters: newParams);

                // S45 熱轉型：只有當使用者帶了 key 且型別真的不同才翻 StrategyType，
                // 避免白白觸發 domain event 與重命名。
                var morphed = false;
                if (isCrossModelMorph)
                {
                    strategy.ChangeType(targetStrategyType!);
                    morphed = true;
                }

                // S45 自動改名：只要 UI 有指定模型，把卡片標題規則統一成
                // `[模型名] 幣種-週期 (Opt)`（S42-S47 膠囊修訂：縮短為 Opt），
                // 視覺上清楚看到「這張卡已套過優化結果」。
                string? newName = null;
                if (modelDisplayName is not null)
                {
                    newName = BuildOptimizedName(modelDisplayName, targetSymbol.BingXFormat, targetInterval);
                    strategy.Rename(newName);
                }

                strategy.UpdateConfiguration(newConfig);
                await repo.UpdateAsync(strategy, ct).ConfigureAwait(false);
                // S53 T2：Lab apply 與 StrategyExecutor / AccountSynchronizer 的寫入可能交錯，
                // 走併發重試版本確保 Stop→Apply→Start 中段的 DbUpdateConcurrencyException 不會炸掉整個流程。
                await uow.SaveChangesWithRetryAsync(ct: ct).ConfigureAwait(false);

                if (wasRunning)
                {
                    // StartAsync 會從 DB 重讀 aggregate、用新的 StrategyType 查 IStrategyFactory，
                    // 因此新大腦會在這一步被實例化 — 無需手動插入 ChangeStrategyTypeAsync 多跑一圈。
                    await controller.StartAsync(strategyId, ct).ConfigureAwait(false);
                }

                // S45-S48 VCP-Realtime-Sync：套用完成的「瞬間」就推播一次最新身分到 Dashboard，
                // 卡片不必等下一根 K 線心跳才跳轉。Name / StrategyType / Symbol / Interval / Leverage
                // 全部同步，對齊 /api/strategies 回傳格式 — Dashboard 收到後用 with-expression 就地更新。
                await broadcaster.BroadcastStrategyMetadataChangedAsync(
                    new StrategyMetadataChangedUpdate(
                        StrategyId: strategyId,
                        Name: strategy.Name,
                        StrategyType: strategy.StrategyType,
                        Symbol: targetSymbol.BingXFormat,
                        Interval: targetInterval.ToString(),
                        Leverage: strategy.Configuration.Leverage.Value),
                    ct).ConfigureAwait(false);

                return Results.Ok(new
                {
                    strategyId,
                    applied = body.Parameters,
                    restarted = wasRunning,
                    morphedTo = morphed ? targetStrategyType : null,
                    renamedTo = newName,
                });
            }
            // S52 T3：任何 Domain 層拒絕（UpdateConfiguration 參數不合法、ChangeType 非法過渡、
            // Rename 空字串…）或已知映射失敗，一律翻成 400 BadRequest 帶清楚訊息。
            catch (DomainException ex)
            {
                logger.LogWarning(ex,
                    "Apply rejected by domain for strategy {Id} (key={Key}): {Msg}",
                    strategyId, body.StrategyKey, ex.Message);
                return Results.BadRequest(new { error = $"參數衝突：{ex.Message}" });
            }
            catch (KeyNotFoundException ex)
            {
                logger.LogWarning(ex,
                    "Apply failed: parameter key missing for strategy {Id} (key={Key}).",
                    strategyId, body.StrategyKey);
                return Results.BadRequest(new { error = $"參數缺失：{ex.Message}" });
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex,
                    "Apply failed with invalid operation for strategy {Id} (key={Key}).",
                    strategyId, body.StrategyKey);
                return Results.BadRequest(new { error = $"邏輯衝突：{ex.Message}" });
            }
        });

        return app;
    }

    /// <summary>
    /// S45：Lab 端的 StrategyKey（UI / catalog 用）→ Domain 層 <c>IStrategy.StrategyType</c> 的字串映射。
    /// 映射必須和 <c>OptimizationOrchestrator.ResolveStrategy</c> 以及各 <c>IStrategy</c> 實作內的
    /// <c>StrategyType</c> 常數保持一致；新增策略時兩邊都要改。找不到映射回 <c>null</c>。
    /// </summary>
    private static string? MapLabKeyToStrategyType(string strategyKey) => strategyKey switch
    {
        "sma"            => "SmaCrossover",
        "trend"          => "TrendFollowing",
        "mean-reversion" => "MeanReversion",
        "rsi-bb"         => "B46RsiBb",
        "pa"             => "PriceAction",
        _                => null,
    };

    /// <summary>
    /// S45 / S42-S47：生成「已套用優化結果」的策略顯示名。
    /// 規則：<c>[模型名] SYMBOL-INTERVAL (Opt)</c>。範例：<c>[B46 Hybrid] SOL-15m (Opt)</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 模型名取的是 <c>StrategyCatalog.DisplayName</c> 的「短形式」— 去掉常見副詞尾（如
    /// <c>" Model"</c>, <c>" Crossover"</c>, <c>" Following"</c>, <c>" Reversion"</c>）讓 UI 卡片標題不會太長。
    /// Symbol 直接用 <c>BingXFormat</c>（例 <c>SOL-USDT</c>）砍掉 <c>-USDT</c>，只留幣種主軸。
    /// </para>
    /// </remarks>
    private static string BuildOptimizedName(string modelDisplayName, string symbolBingXFormat, KlineInterval interval)
    {
        // 短形式：只留模型識別字，不帶通用尾詞。找不到就退回原字串，絕不丟擲。
        string shortModel = modelDisplayName;
        foreach (var suffix in new[] { " Model", " Crossover", " Trend Following", " Reversion", " Hybrid Model", " Predictor" })
        {
            if (shortModel.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                shortModel = shortModel[..^suffix.Length].Trim();
                break;
            }
        }

        // 幣種顯示形式：SOL-USDT → SOL；其他格式保留原字串（防禦性）
        var dashIdx = symbolBingXFormat.IndexOf('-');
        var coin = dashIdx > 0 ? symbolBingXFormat[..dashIdx] : symbolBingXFormat;

        var intervalLabel = FormatInterval(interval);

        return $"[{shortModel}] {coin}-{intervalLabel} (Opt)";
    }

    /// <summary>
    /// S45：KlineInterval → UI 短標籤（<c>FifteenMinutes → 15m</c>, <c>OneHour → 1h</c>）。
    /// 對齊 <c>BacktestLab.razor</c> 的 <c>IntervalLabel</c>，讓 Dashboard 卡片名稱與 Lab 排行榜 interval 欄看到的一致。
    /// </summary>
    private static string FormatInterval(KlineInterval interval) => interval switch
    {
        KlineInterval.OneMinute      => "1m",
        KlineInterval.ThreeMinutes   => "3m",
        KlineInterval.FiveMinutes    => "5m",
        KlineInterval.FifteenMinutes => "15m",
        KlineInterval.ThirtyMinutes  => "30m",
        KlineInterval.OneHour        => "1h",
        KlineInterval.TwoHours       => "2h",
        KlineInterval.FourHours      => "4h",
        KlineInterval.SixHours       => "6h",
        KlineInterval.EightHours     => "8h",
        KlineInterval.TwelveHours    => "12h",
        KlineInterval.OneDay         => "1d",
        KlineInterval.ThreeDays      => "3d",
        KlineInterval.OneWeek        => "1w",
        KlineInterval.OneMonth       => "1M",
        _                             => interval.ToString(),
    };

    private static bool ValidateRequest(OptimizationRequest r, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(r.StrategyKey)) { error = "StrategyKey is required."; return false; }
        if (r.Ranges is null || r.Ranges.Count == 0)  { error = "At least one parameter range required."; return false; }
        if (r.StartUtc >= r.EndUtc)                   { error = "Start must be before End."; return false; }

        // S22-UI：全局掃描參數驗證。UI 已先驗一次，但 API 端也要守住 — 避免外部直呼 API 繞過。
        if (string.IsNullOrWhiteSpace(r.Symbol)) { error = "Symbol is required."; return false; }
        try { _ = Symbol.Parse(r.Symbol); }
        catch (DomainException ex) { error = $"Invalid Symbol '{r.Symbol}': {ex.Message}"; return false; }

        if (r.SlippageBps < 0m) { error = "SlippageBps must be non-negative."; return false; }
        if (r.InitialBalance <= 0m) { error = "InitialBalance must be positive."; return false; }
        if (r.Leverage < 1 || r.Leverage > 100) { error = "Leverage must be between 1 and 100."; return false; }

        foreach (var range in r.Ranges)
        {
            if (range.Step <= 0) { error = $"Step must be positive for '{range.Name}'."; return false; }
            if (range.Max < range.Min) { error = $"Max must be >= Min for '{range.Name}'."; return false; }
        }

        // S67/S69：Random 與 Bayesian 共用 RandomBudget 欄位控制 trial 數，皆需帶正整數；Grid 不要求 budget。
        if (r.SearchMethod == SearchMethod.Random || r.SearchMethod == SearchMethod.Bayesian)
        {
            if (r.RandomBudget is null || r.RandomBudget <= 0)
            {
                error = $"RandomBudget must be a positive integer when SearchMethod={r.SearchMethod}.";
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// S25 T1：上次優化存檔回應。Score 目前以 ReturnPercent 填值（語意：百分比，相較初始資金）。
/// </summary>
public sealed record CachedSettingsDto(
    string StrategyKey,
    string Symbol,
    KlineInterval Interval,
    IReadOnlyDictionary<string, decimal> Parameters,
    decimal Score,
    DateTime UpdatedAtUtc);
