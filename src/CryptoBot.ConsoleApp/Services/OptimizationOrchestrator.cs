using System.Text.Json;
using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Backtesting.Search;
using CryptoBot.Application.Strategies;
using CryptoBot.Application.Strategies.B46RsiBb;
using CryptoBot.Application.Strategies.MeanReversion;
using CryptoBot.Application.Strategies.PriceAction;
using CryptoBot.Application.Strategies.SmaCrossover;
using CryptoBot.Application.Strategies.TrendFollowing;
using CryptoBot.ConsoleApp.Lab;
using CryptoBot.ConsoleApp.Realtime;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Aggregates.StrategyOptimizationAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Backtesting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// Web 版的優化指揮中心 — 把 CLI 版 BacktestRunner.RunOptimizationAsync 拆成可以被網頁觸發、
/// 跑到一半會回報進度、跑完會廣播排行榜的非同步 job。
///
/// 設計：
/// - Singleton。用 <see cref="SemaphoreSlim"/>(1,1) 當閘門，同時只能有一個優化任務在跑。
/// - 透過 <see cref="DashboardEventBus"/> 把進度 / 完成 / 失敗事件推到 Blazor + TradeHub。
/// - 每組參數自己 CreateScope — 跟 CLI 版同樣的執行緒安全保證（不共享 DbContext）。
/// </summary>
public sealed class OptimizationOrchestrator
{
    private const int WarmupBars = 120;
    private const int MinFillsForRanking = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DashboardEventBus _bus;
    private readonly IHubContext<TradeHub> _hub;
    private readonly ILogger<OptimizationOrchestrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OptimizationOrchestrator(
        IServiceScopeFactory scopeFactory,
        DashboardEventBus bus,
        IHubContext<TradeHub> hub,
        ILogger<OptimizationOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _hub = hub;
        _logger = logger;
    }

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>
    /// 嘗試啟動一次優化任務。若已有任務在跑，立即回 false 不排隊。
    /// 真正的回測在背景 Task.Run 裡跑 — 呼叫端（Endpoint）馬上收到 true/false。
    /// </summary>
    public bool TryStart(OptimizationRequest request)
    {
        if (!_gate.Wait(0)) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Optimization job crashed.");
                await BroadcastFailedAsync(ex.Message).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        });

        return true;
    }

    private async Task RunAsync(OptimizationRequest req, CancellationToken ct)
    {
        // 解析一次 Symbol VO — 後面下載歷史 / 建 StrategyConfiguration 都共用同一份，
        // 避免每次重複 Parse 的成本與語意漂移。API / UI 端已先做了格式驗證，這裡會拋就是 bug。
        var symbol = Symbol.Parse(req.Symbol);

        _logger.LogInformation(
            "🧪 [LAB] Optimization requested — strategy={StrategyKey}, symbol={Symbol}, interval={Interval}, slippage={Slippage}bp, initBal={InitBal}, leverage={Lev}x, {RangeCount} param ranges, window {Start}→{End}",
            req.StrategyKey, symbol.BingXFormat, req.Interval, req.SlippageBps, req.InitialBalance,
            req.Leverage, req.Ranges.Count, req.StartUtc, req.EndUtc);

        // 1) 先把 OHLC 下載到 SQLite（跟 CLI 版同一條路徑）
        await EnsureHistoricalAsync(symbol, req.Interval, req.StartUtc, req.EndUtc, ct).ConfigureAwait(false);

        // 2) 展開參數網格（每個策略自己的 param 清單已由 BuildRequest 組好）
        var ranges = req.Ranges
            .Select(r => new ParameterRange(r.Name, r.Min, r.Max, r.Step))
            .ToArray();

        // 3) 依搜尋方法計算進度總數（Random/Bayesian=budget；Grid=笛卡兒積大小）
        var total = req.SearchMethod switch
        {
            SearchMethod.Random or SearchMethod.Bayesian => req.RandomBudget ?? 0,
            _ => ranges.Aggregate(1, (acc, r) => acc * r.Enumerate().Count()),
        };
        var completed = 0;

        // 4) 初始進度 0 / total
        await BroadcastProgressAsync(0, total, "starting…").ConfigureAwait(false);

        var optimizer = new StrategyOptimizer(
            _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<StrategyOptimizer>());

        // 共用 runOne 委派：Grid/Random/Bayesian 三條路徑共用同一個回測 + 進度廣播閉包，差異只在誰出題。
        Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<BacktestReport>> runOne =
            async (paramSet, token) =>
            {
                BacktestReport report;
                if (!IsValidCombination(req.StrategyKey, paramSet))
                {
                    // 無效組合（例如 SMA 的 Fast ≥ Slow）— 直接回空報告，不浪費算力
                    report = EmptyReport(req.InitialBalance);
                }
                else
                {
                    using var scope = _scopeFactory.CreateScope();
                    report = await RunOneBacktestAsync(scope.ServiceProvider, req, symbol, paramSet, token)
                        .ConfigureAwait(false);
                }

                var done = Interlocked.Increment(ref completed);
                await BroadcastProgressAsync(done, total, FormatParamSet(paramSet))
                    .ConfigureAwait(false);

                return report;
            };

        IReadOnlyList<OptimizationRun> runs;
        if (req.SearchMethod == SearchMethod.Bayesian)
        {
            // 貝氏優化走 IAdaptiveSearchStrategy 路徑（序列、邊跑邊建議），strategy 由 DI 提供。
            using var scope = _scopeFactory.CreateScope();
            var adaptive = scope.ServiceProvider.GetRequiredService<IAdaptiveSearchStrategy>();
            var budget = req.RandomBudget
                ?? throw new InvalidOperationException(
                    "RandomBudget must be specified for SearchMethod=Bayesian.");
            runs = await optimizer.RunAsync(ranges, adaptive, budget, runOne, ct).ConfigureAwait(false);
        }
        else
        {
            ISearchStrategy search = req.SearchMethod switch
            {
                SearchMethod.Random => new RandomSearchStrategy(
                    budget: req.RandomBudget
                        ?? throw new InvalidOperationException(
                            "RandomBudget must be specified for SearchMethod=Random.")),
                _ => new GridSearchStrategy(),
            };
            runs = await optimizer.RunAsync(ranges, search, runOne, ct: ct).ConfigureAwait(false);
        }

        // 5) 排名 + 推送完成事件 + 持久化 Top1 到 StrategyOptimizationSettings
        await BroadcastCompletedAsync(req, symbol, runs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 策略特定的組合合法性檢查。SMA 要求 Fast &lt; Slow；其他策略目前無額外限制。
    /// </summary>
    private static bool IsValidCombination(string strategyKey, IReadOnlyDictionary<string, decimal> paramSet)
    {
        if (strategyKey == "sma")
        {
            var fast = (int)paramSet["FastSmaPeriod"];
            var slow = (int)paramSet["SlowSmaPeriod"];
            return fast < slow;
        }
        if (strategyKey == "rsi-bb")
        {
            // oversold < overbought 是語義前提；其他維度任意組合都算有效。
            var oversold   = paramSet.GetValueOrDefault("RsiOversold",   30m);
            var overbought = paramSet.GetValueOrDefault("RsiOverbought", 70m);
            return oversold < overbought;
        }
        if (strategyKey == "trend")
        {
            // 快 EMA 必須短於慢 EMA，否則黃金/死亡交叉訊號反向。
            var fast = (int)paramSet["FastEmaPeriod"];
            var slow = (int)paramSet["SlowEmaPeriod"];
            return fast < slow;
        }
        if (strategyKey == "mean-reversion")
        {
            // 超賣線必須低於超買線（否則無反轉窗口）。
            var oversold   = paramSet.GetValueOrDefault("RsiOversold",   30m);
            var overbought = paramSet.GetValueOrDefault("RsiOverbought", 70m);
            return oversold < overbought;
        }
        return true;
    }

    private static string FormatParamSet(IReadOnlyDictionary<string, decimal> paramSet) =>
        string.Join(", ", paramSet.Select(kv =>
            kv.Value == Math.Floor(kv.Value)
                ? $"{kv.Key}={(int)kv.Value}"
                : $"{kv.Key}={kv.Value}"));

    /// <summary>
    /// S23：智慧填充 — 下載前先查本地已存範圍，只抓缺的前段 (Backward) 與後段 (Forward)。
    ///
    /// 三種情境：
    /// <list type="bullet">
    ///   <item>本地無資料 → 下載 [start, end] 全段。</item>
    ///   <item>本地已覆蓋 [start, end] → 跳過下載，直接走回測。</item>
    ///   <item>部分覆蓋 → 只補 backward [start, earliest-step] 與 forward [latest+step, end]。</item>
    /// </list>
    /// 隔離鍵維持 (Symbol, Interval) — 不同組合各自計算 gap，避免污染彼此的快取。
    /// </summary>
    private async Task EnsureHistoricalAsync(
        Symbol symbol, KlineInterval interval, DateTime start, DateTime end, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IHistoricalDataProvider>();
        var store = scope.ServiceProvider.GetRequiredService<IHistoricalKlineStore>();

        var (earliest, latest) = await store.GetStoredRangeAsync(symbol, interval, ct).ConfigureAwait(false);
        var step = interval.ToTimeSpan();

        if (earliest is null || latest is null)
        {
            _logger.LogInformation(
                "🧪 [LAB] No local cache for {Symbol} {Interval} — full download [{Start:yyyy-MM-dd} .. {End:yyyy-MM-dd}].",
                symbol.BingXFormat, interval, start, end);
            await DownloadSegmentAsync(provider, store, symbol, interval, start, end, ct).ConfigureAwait(false);
            return;
        }

        // 已完整覆蓋 — 跳過（§VCP-Functional：相同區間的第二次掃描不再命中 REST）
        if (earliest.Value <= start && latest.Value >= end)
        {
            _logger.LogInformation(
                "🧪 [LAB] Cache HIT {Symbol} {Interval} covers [{Start:yyyy-MM-dd} .. {End:yyyy-MM-dd}] — skip download.",
                symbol.BingXFormat, interval, start, end);
            return;
        }

        // Backward fill：本地最早 > 請求起點 → 補 [start, earliest - step]
        if (earliest.Value > start)
        {
            var backEnd = earliest.Value - step;
            if (backEnd > start)
            {
                _logger.LogInformation(
                    "🧪 [LAB] Backward-fill {Symbol} {Interval} [{Start:yyyy-MM-dd} .. {End:yyyy-MM-dd}].",
                    symbol.BingXFormat, interval, start, backEnd);
                await DownloadSegmentAsync(provider, store, symbol, interval, start, backEnd, ct).ConfigureAwait(false);
            }
        }

        // Forward fill：本地最新 < 請求終點 → 補 [latest + step, end]
        if (latest.Value < end)
        {
            var fwdStart = latest.Value + step;
            if (fwdStart < end)
            {
                _logger.LogInformation(
                    "🧪 [LAB] Forward-fill {Symbol} {Interval} [{Start:yyyy-MM-dd} .. {End:yyyy-MM-dd}].",
                    symbol.BingXFormat, interval, fwdStart, end);
                await DownloadSegmentAsync(provider, store, symbol, interval, fwdStart, end, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task DownloadSegmentAsync(
        IHistoricalDataProvider provider,
        IHistoricalKlineStore store,
        Symbol symbol, KlineInterval interval,
        DateTime segStart, DateTime segEnd,
        CancellationToken ct)
    {
        await foreach (var batch in provider.DownloadAsync(symbol, interval, segStart, segEnd, ct).ConfigureAwait(false))
        {
            await store.UpsertAsync(symbol, interval, batch, ct).ConfigureAwait(false);
        }
    }

    private static async Task<BacktestReport> RunOneBacktestAsync(
        IServiceProvider scoped,
        OptimizationRequest req,
        Symbol symbol,
        IReadOnlyDictionary<string, decimal> paramSet,
        CancellationToken ct)
    {
        var store = scoped.GetRequiredService<IHistoricalKlineStore>();
        var loggerFactory = scoped.GetRequiredService<ILoggerFactory>();

        var options = new BacktestOptions
        {
            Symbol = symbol.BingXFormat,
            Interval = req.Interval,
            StartTime = req.StartUtc,
            EndTime = req.EndUtc,
            InitialBalance = req.InitialBalance,
            SlippageBps = req.SlippageBps,
            CommissionRate = 0.0005m,
            WarmupBars = WarmupBars,
        };

        // S32-T3：Lab 可指定 1..100x 槓桿（爆倉模擬所需），因此這裡顯式放寬 max=100；
        // Leverage VO 預設 max=20 是給 live 交易用的保守護欄，跟回測沙盒的風險偏好不同。
        var config = StrategyConfiguration.Create(
            symbol: symbol,
            interval: req.Interval,
            leverage: Leverage.Create(req.Leverage, max: 100),
            maxKlineWindow: WarmupBars,
            parameters: new Dictionary<string, decimal>(paramSet));

        var simulator = new BacktestSimulator(options, loggerFactory.CreateLogger<BacktestSimulator>());
        IStrategy strategy = ResolveStrategy(req.StrategyKey);
        var engine = new BacktestEngine(
            store: store, clock: simulator, exchange: simulator, strategy: strategy,
            logger: loggerFactory.CreateLogger<BacktestEngine>());

        return await engine.RunAsync(options, config, Guid.NewGuid(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 策略插槽派發：根據 StrategyCatalog 的 Key 建立對應的 <see cref="IStrategy"/>。
    /// 新策略要上線優化時在這裡加一個 case。
    /// </summary>
    private static IStrategy ResolveStrategy(string strategyKey) => strategyKey switch
    {
        "sma"            => new SmaCrossoverStrategy(),
        "rsi-bb"         => new B46RsiBbStrategy(),
        "trend"          => new TrendFollowingStrategy(),
        "mean-reversion" => new MeanReversionStrategy(),
        "pa"             => new PriceActionPredictorStrategy(),
        _                => throw new ArgumentException($"Unknown strategy key: {strategyKey}", nameof(strategyKey))
    };

    private static BacktestReport EmptyReport(decimal initialBalance) => new(
        TotalKlines: 0, SignalsTriggered: 0, OrdersFilled: 0,
        StartingBalance: initialBalance, EndingBalance: initialBalance,
        PeakEquity: initialBalance, MaxDrawdownPercent: 0m,
        FirstKlineTime: null, LastKlineTime: null,
        Fills: Array.Empty<CryptoBot.Domain.Aggregates.OrderAggregate.Order>(),
        EquityCurve: Array.Empty<EquityPoint>());

    // ──────────────────────────────────────────────
    //  廣播：本地 EventBus + SignalR Hub（雙通路）
    // ──────────────────────────────────────────────

    private async Task BroadcastProgressAsync(int done, int total, string currentParams)
    {
        var update = new OptimizationProgressUpdate(done, total, currentParams);
        _bus.RaiseOptimizationProgress(update);
        await _hub.Clients.All.SendAsync("OptimizationProgress", update).ConfigureAwait(false);
    }

    private async Task BroadcastCompletedAsync(
        OptimizationRequest req, Symbol symbol, IReadOnlyList<OptimizationRun> runs, CancellationToken ct)
    {
        // S32-T2：爆倉視為最差績效，但不可被 MinFillsForRanking 濾掉 — 即使成交數少也要讓使用者看到爆倉事實。
        //
        // 排序規則：先把所有「有效」結果（非爆倉且成交數 ≥ 門檻）按 ProfitToDrawdownRatio 由大到小排，
        // 爆倉結果一律壓到最底層；爆倉彼此之間再按 ReturnPercent（本應全是 -100%）做 tie-break。
        // 這樣 Leaderboard Rank 1 永遠是最好的可交易結果，爆倉被清楚標註在尾端讓人看得到「這組必死」。
        var ranked = runs
            .Where(r => r.Report.IsLiquidated || r.Report.OrdersFilled >= MinFillsForRanking)
            .OrderBy(r => r.Report.IsLiquidated ? 1 : 0)
            .ThenByDescending(r => ProfitToDrawdownRatio(r.Report))
            .ThenByDescending(r => r.Report.ReturnPercent)
            .ToList();

        var rows = ranked.Select((r, idx) =>
        {
            var ratio = ProfitToDrawdownRatio(r.Report);
            var isInf = ratio == decimal.MaxValue;
            var paramDict = new Dictionary<string, decimal>(r.Parameters);
            // S25 T2 / S26 T3：Rank 1 才帶 EquityCurve + FillMarkers — SignalR payload 節流。
            var equityCurve = idx == 0
                ? r.Report.EquityCurve
                : Array.Empty<EquityPoint>();
            IReadOnlyList<FillMarkerDto> markers = idx == 0
                ? r.Report.Fills.Select(o => new FillMarkerDto(
                      CreatedAt: o.CreatedAt,
                      PositionSide: o.PositionSide.ToString(),
                      Side: o.Side.ToString(),
                      Price: o.AverageFillPrice?.Value ?? 0m,
                      Quantity: o.Quantity.Value)).ToList()
                : Array.Empty<FillMarkerDto>();
            return new LeaderboardRowDto(
                Rank: idx + 1,
                Parameters: paramDict,
                ParameterSummary: FormatSummary(req.StrategyKey, paramDict),
                NetPnL: r.Report.NetPnL,
                ReturnPercent: r.Report.ReturnPercent,
                MaxDrawdownPercent: r.Report.MaxDrawdownPercent,
                ProfitToDrawdownRatio: isInf ? 0m : ratio,
                IsInfiniteRatio: isInf,
                Fills: r.Report.OrdersFilled,
                EquityCurve: equityCurve,
                SharpeRatio: r.Report.SharpeRatio,
                FillMarkers: markers,
                IsLiquidated: r.Report.IsLiquidated);
        }).ToList();

        var update = new OptimizationCompletedUpdate(
            TotalRuns: runs.Count,
            Shown: rows.Count,
            FilteredOut: runs.Count - rows.Count,
            Rows: rows);

        // S25 T1：把 Top 1 持久化到 StrategyOptimizationSettings（複合鍵 StrategyKey→Guid / Symbol / Interval）
        if (rows.Count > 0)
        {
            await PersistTopResultAsync(req, symbol, rows[0], ct).ConfigureAwait(false);
        }

        _bus.RaiseOptimizationCompleted(update);
        await _hub.Clients.All.SendAsync("OptimizationCompleted", update).ConfigureAwait(false);

        _logger.LogInformation(
            "🧪 [LAB] Optimization completed — {Shown}/{Total} valid runs.",
            rows.Count, runs.Count);
    }

    /// <summary>
    /// S25 T1：把 Leaderboard Rank 1 的參數 + Return% 寫入 StrategyOptimizationSettings。
    /// 複合鍵 (StrategyKey→Guid via <see cref="LabStrategyKey"/>, Symbol, Interval) — 同條件重跑會覆蓋。
    /// </summary>
    private async Task PersistTopResultAsync(
        OptimizationRequest req, Symbol symbol, LeaderboardRowDto top, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyOptimizationSettingsRepository>();
            var uow  = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var parametersJson = JsonSerializer.Serialize(top.Parameters);
            var settings = StrategyOptimizationSettings.Create(
                strategyId: LabStrategyKey.ToGuid(req.StrategyKey),
                symbol: symbol,
                interval: req.Interval,
                parametersJson: parametersJson,
                score: top.ReturnPercent,
                updatedAtUtc: DateTime.UtcNow);

            await repo.UpsertAsync(settings, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "💾 [LAB] Persisted top result — key={Key} symbol={Symbol} interval={Interval} score={Score:F2}%",
                req.StrategyKey, symbol.BingXFormat, req.Interval, top.ReturnPercent);
        }
        catch (Exception ex)
        {
            // 持久化失敗不該拖垮廣播；log 即可，前端仍會收到 Leaderboard。
            _logger.LogWarning(ex,
                "⚠ [LAB] Failed to persist top result for {Key}/{Symbol}/{Interval}.",
                req.StrategyKey, symbol.BingXFormat, req.Interval);
        }
    }

    private static string FormatSummary(string strategyKey, IReadOnlyDictionary<string, decimal> p)
    {
        if (strategyKey == "sma")
            return $"Fast={(int)p.GetValueOrDefault("FastSmaPeriod")} / Slow={(int)p.GetValueOrDefault("SlowSmaPeriod")}";
        if (strategyKey == "rsi-bb")
            return $"RSI={(int)p.GetValueOrDefault("RsiPeriod")} ({(int)p.GetValueOrDefault("RsiOversold")}/{(int)p.GetValueOrDefault("RsiOverbought")}) · BB={(int)p.GetValueOrDefault("BbPeriod")}±{p.GetValueOrDefault("BbStdDev"):0.##}";
        if (strategyKey == "trend")
            return $"EMA {(int)p.GetValueOrDefault("FastEmaPeriod")}/{(int)p.GetValueOrDefault("SlowEmaPeriod")} · RSI={(int)p.GetValueOrDefault("RsiPeriod")}@{(int)p.GetValueOrDefault("RsiMidline")}";
        if (strategyKey == "mean-reversion")
            return $"BB={(int)p.GetValueOrDefault("BbPeriod")}±{p.GetValueOrDefault("BbStdDev"):0.##} · RSI={(int)p.GetValueOrDefault("RsiPeriod")} ({(int)p.GetValueOrDefault("RsiOversold")}/{(int)p.GetValueOrDefault("RsiOverbought")})";
        if (strategyKey == "pa")
            return $"LB={(int)p.GetValueOrDefault("LookbackPeriod")} · Mom≥{p.GetValueOrDefault("MomentumThreshold"):0.###} · Wick/Body×{p.GetValueOrDefault("WickToBodyRatio"):0.##} · Engulf={(p.GetValueOrDefault("EngulfingEnabled") > 0 ? "ON" : "OFF")}";
        return FormatParamSet(p);
    }

    private async Task BroadcastFailedAsync(string error)
    {
        var update = new OptimizationFailedUpdate(error);
        _bus.RaiseOptimizationFailed(update);
        await _hub.Clients.All.SendAsync("OptimizationFailed", update).ConfigureAwait(false);
    }

    private static decimal ProfitToDrawdownRatio(BacktestReport r)
    {
        if (r.MaxDrawdownPercent <= 0m)
            return r.NetPnL > 0m ? decimal.MaxValue : 0m;
        return r.NetPnL / r.MaxDrawdownPercent;
    }
}

/// <summary>
/// 單一參數掃描範圍 DTO — 序列化後給前端表單 / API 用，對應 Application 層的
/// <see cref="ParameterRange"/>（後者有 Enumerate 邏輯，是引擎用的）。
/// </summary>
public sealed record ParameterRangeDto(string Name, decimal Min, decimal Max, decimal Step);

/// <summary>
/// 優化請求 — StrategyKey 決定 Orchestrator 要派哪一個 IStrategy + 驗哪些組合限制。
/// Ranges 是該策略可掃描的參數集合（笛卡兒展開給 BacktestEngine 跑）。
///
/// S22-UI Lab Upgrade：解鎖 Symbol / Interval / SlippageBps / InitialBalance，
/// 這四個維度先前在 Orchestrator 裡寫死（BTC-USDT / 1h / 5bp / 10000），
/// 現在由 UI 提供，Orchestrator 只消費。
/// </summary>
public sealed record OptimizationRequest(
    string StrategyKey,
    IReadOnlyList<ParameterRangeDto> Ranges,
    DateTime StartUtc,
    DateTime EndUtc,
    string Symbol,
    KlineInterval Interval,
    decimal SlippageBps,
    decimal InitialBalance,
    int Leverage = 1,
    // S67：可插拔搜尋演算法。預設 Grid 維持向後相容（舊 client / 未升級 form 不送這欄就走網格）。
    SearchMethod SearchMethod = SearchMethod.Grid,
    // Random 模式必填（取樣次數）；Grid 模式忽略。LabEndpoints.ValidateRequest 把關必填條件。
    int? RandomBudget = null);

/// <summary>
/// Leaderboard 套用請求 — 整包參數字典直接灌進 StrategyConfiguration.Parameters。
/// 策略自己用 GetParameter("X", default) 取自己要的 key，因此這層不必知道策略型別。
///
/// S45（修訂版 S42-S47）：新增可選的 <c>StrategyKey</c> / <c>Symbol</c> / <c>Interval</c>。
/// 當 UI 在實驗室選了不同模型 / 幣種 / 週期並套用時，後端一次把四件事改齊：
/// <list type="bullet">
///   <item><c>StrategyType</c>（熱轉型）</item>
///   <item><c>Symbol</c>（切換交易對）</item>
///   <item><c>Interval</c>（切換 K 線週期）</item>
///   <item><c>Parameters</c>（套用優化結果）</item>
/// </list>
/// 並自動改名為 <c>[模型名] 幣種-週期 (Opt)</c>。
/// 三個可選欄位全為 null 時維持 S25 行為（僅套參數、不改型/市場/週期）。
/// </summary>
public sealed record ApplyParamsRequest(
    IReadOnlyDictionary<string, decimal> Parameters,
    string? StrategyKey = null,
    string? Symbol = null,
    KlineInterval? Interval = null);

/// <summary>
/// UI 全局掃描參數 — 不屬於任何單一策略、卻對所有策略都生效（標的、週期、滑價、起始資金）。
///
/// 為什麼獨立一個 record：<see cref="StrategyParameterFormBase.BuildRequest"/> 以前只吃時間窗，
/// 後來 S22-UI Lab Upgrade 讓 UI 可以動標的 / 週期 / 滑價 / 資金，但這些不該污染策略專屬表單的欄位。
/// 用這個 record 統一攜帶「跨策略的掃描環境」，將來再加欄位（例如手續費率）也只改這裡。
/// </summary>
public sealed record OptimizationGlobals(
    string Symbol,
    KlineInterval Interval,
    decimal SlippageBps,
    decimal InitialBalance,
    int Leverage = 1);
