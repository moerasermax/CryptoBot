using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Strategies;
using CryptoBot.Application.Strategies.B46RsiBb;
using CryptoBot.Application.Strategies.SmaCrossover;
using CryptoBot.ConsoleApp.Realtime;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
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
    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC-USDT");
    private const KlineInterval Interval = KlineInterval.OneHour;
    private const int WarmupBars = 120;
    private const decimal InitialBalance = 10_000m;
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
        _logger.LogInformation(
            "🧪 [LAB] Optimization requested — strategy={StrategyKey}, {RangeCount} param ranges, window {Start}→{End}",
            req.StrategyKey, req.Ranges.Count, req.StartUtc, req.EndUtc);

        // 1) 先把 OHLC 下載到 SQLite（跟 CLI 版同一條路徑）
        await EnsureHistoricalAsync(req.StartUtc, req.EndUtc, ct).ConfigureAwait(false);

        // 2) 展開參數網格（每個策略自己的 param 清單已由 BuildRequest 組好）
        var ranges = req.Ranges
            .Select(r => new ParameterRange(r.Name, r.Min, r.Max, r.Step))
            .ToArray();

        // 3) 先算總數，才能在每次完成時推「x / total」
        var total = ranges.Aggregate(1, (acc, r) => acc * r.Enumerate().Count());
        var completed = 0;

        // 4) 初始進度 0 / total
        await BroadcastProgressAsync(0, total, "starting…").ConfigureAwait(false);

        var optimizer = new StrategyOptimizer(
            _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<StrategyOptimizer>());

        var runs = await optimizer.RunAsync(
            ranges,
            runOne: async (paramSet, token) =>
            {
                BacktestReport report;
                if (!IsValidCombination(req.StrategyKey, paramSet))
                {
                    // 無效組合（例如 SMA 的 Fast ≥ Slow）— 直接回空報告，不浪費算力
                    report = EmptyReport();
                }
                else
                {
                    using var scope = _scopeFactory.CreateScope();
                    report = await RunOneBacktestAsync(scope.ServiceProvider, req, paramSet, token)
                        .ConfigureAwait(false);
                }

                var done = Interlocked.Increment(ref completed);
                await BroadcastProgressAsync(done, total, FormatParamSet(paramSet))
                    .ConfigureAwait(false);

                return report;
            },
            ct: ct).ConfigureAwait(false);

        // 5) 排名 + 推送完成事件
        await BroadcastCompletedAsync(req.StrategyKey, runs).ConfigureAwait(false);
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
        return true;
    }

    private static string FormatParamSet(IReadOnlyDictionary<string, decimal> paramSet) =>
        string.Join(", ", paramSet.Select(kv =>
            kv.Value == Math.Floor(kv.Value)
                ? $"{kv.Key}={(int)kv.Value}"
                : $"{kv.Key}={kv.Value}"));

    private async Task EnsureHistoricalAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IHistoricalDataProvider>();
        var store = scope.ServiceProvider.GetRequiredService<IHistoricalKlineStore>();

        await foreach (var batch in provider.DownloadAsync(BtcUsdt, Interval, start, end, ct).ConfigureAwait(false))
        {
            await store.UpsertAsync(BtcUsdt, Interval, batch, ct).ConfigureAwait(false);
        }
    }

    private static async Task<BacktestReport> RunOneBacktestAsync(
        IServiceProvider scoped,
        OptimizationRequest req,
        IReadOnlyDictionary<string, decimal> paramSet,
        CancellationToken ct)
    {
        var store = scoped.GetRequiredService<IHistoricalKlineStore>();
        var loggerFactory = scoped.GetRequiredService<ILoggerFactory>();

        var options = new BacktestOptions
        {
            Symbol = BtcUsdt.BingXFormat,
            Interval = Interval,
            StartTime = req.StartUtc,
            EndTime = req.EndUtc,
            InitialBalance = InitialBalance,
            SlippageBps = 5m,
            CommissionRate = 0.0005m,
            WarmupBars = WarmupBars,
        };

        var config = StrategyConfiguration.Create(
            symbol: BtcUsdt,
            interval: Interval,
            leverage: Leverage.Conservative,
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
        "sma"    => new SmaCrossoverStrategy(),
        "rsi-bb" => new B46RsiBbStrategy(),
        _        => throw new ArgumentException($"Unknown strategy key: {strategyKey}", nameof(strategyKey))
    };

    private static BacktestReport EmptyReport() => new(
        TotalKlines: 0, SignalsTriggered: 0, OrdersFilled: 0,
        StartingBalance: InitialBalance, EndingBalance: InitialBalance,
        PeakEquity: InitialBalance, MaxDrawdownPercent: 0m,
        FirstKlineTime: null, LastKlineTime: null,
        Fills: Array.Empty<CryptoBot.Domain.Aggregates.OrderAggregate.Order>());

    // ──────────────────────────────────────────────
    //  廣播：本地 EventBus + SignalR Hub（雙通路）
    // ──────────────────────────────────────────────

    private async Task BroadcastProgressAsync(int done, int total, string currentParams)
    {
        var update = new OptimizationProgressUpdate(done, total, currentParams);
        _bus.RaiseOptimizationProgress(update);
        await _hub.Clients.All.SendAsync("OptimizationProgress", update).ConfigureAwait(false);
    }

    private async Task BroadcastCompletedAsync(string strategyKey, IReadOnlyList<OptimizationRun> runs)
    {
        var ranked = runs
            .Where(r => r.Report.OrdersFilled >= MinFillsForRanking)
            .OrderByDescending(r => ProfitToDrawdownRatio(r.Report))
            .ToList();

        var rows = ranked.Select((r, idx) =>
        {
            var ratio = ProfitToDrawdownRatio(r.Report);
            var isInf = ratio == decimal.MaxValue;
            var paramDict = new Dictionary<string, decimal>(r.Parameters);
            return new LeaderboardRowDto(
                Rank: idx + 1,
                Parameters: paramDict,
                ParameterSummary: FormatSummary(strategyKey, paramDict),
                NetPnL: r.Report.NetPnL,
                ReturnPercent: r.Report.ReturnPercent,
                MaxDrawdownPercent: r.Report.MaxDrawdownPercent,
                ProfitToDrawdownRatio: isInf ? 0m : ratio,
                IsInfiniteRatio: isInf,
                Fills: r.Report.OrdersFilled);
        }).ToList();

        var update = new OptimizationCompletedUpdate(
            TotalRuns: runs.Count,
            Shown: rows.Count,
            FilteredOut: runs.Count - rows.Count,
            Rows: rows);

        _bus.RaiseOptimizationCompleted(update);
        await _hub.Clients.All.SendAsync("OptimizationCompleted", update).ConfigureAwait(false);

        _logger.LogInformation(
            "🧪 [LAB] Optimization completed — {Shown}/{Total} valid runs.",
            rows.Count, runs.Count);
    }

    private static string FormatSummary(string strategyKey, IReadOnlyDictionary<string, decimal> p)
    {
        if (strategyKey == "sma")
            return $"Fast={(int)p.GetValueOrDefault("FastSmaPeriod")} / Slow={(int)p.GetValueOrDefault("SlowSmaPeriod")}";
        if (strategyKey == "rsi-bb")
            return $"RSI={(int)p.GetValueOrDefault("RsiPeriod")} ({(int)p.GetValueOrDefault("RsiOversold")}/{(int)p.GetValueOrDefault("RsiOverbought")}) · BB={(int)p.GetValueOrDefault("BbPeriod")}±{p.GetValueOrDefault("BbStdDev"):0.##}";
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
/// </summary>
public sealed record OptimizationRequest(
    string StrategyKey,
    IReadOnlyList<ParameterRangeDto> Ranges,
    DateTime StartUtc,
    DateTime EndUtc);

/// <summary>
/// Leaderboard 套用請求 — 整包參數字典直接灌進 StrategyConfiguration.Parameters。
/// 策略自己用 GetParameter("X", default) 取自己要的 key，因此這層不必知道策略型別。
/// </summary>
public sealed record ApplyParamsRequest(IReadOnlyDictionary<string, decimal> Parameters);
