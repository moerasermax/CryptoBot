using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Strategies;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Backtesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// CLI 入口：<c>dotnet run -- backtest</c> 及其變體的一次性流程。
///
/// 子路徑：
///   - 單次回測（預設）：下載 30d 資料 → 指定策略跑一次 → 印單份報告（含 Sharpe）
///   - 參數優化（<c>--optimize</c>）：展開 FastSma × SlowSma 笛卡兒積，Parallel.ForEachAsync 掃完後印排行榜
///
/// CAP-003 Phase 0a — 支援 CLI args：
///   --strategy &lt;type&gt;            策略類型字串（不分大小寫、允許 dash，如 trend-following ↔ TrendFollowing）
///   --symbol &lt;BASE-QUOTE&gt;        交易對（預設 BTC-USDT）
///   --interval &lt;1m|15m|1h|4h|1d&gt;  K 線週期（預設 1h）
///   --params Key=Val,Key=Val,...   策略參數（注入 StrategyConfiguration.Parameters）
/// 未帶任何 args → 沿用原 SmaCrossover 20/50 預設行為（向後相容）。
///
/// 兩條路徑共用：<see cref="IHistoricalDataProvider"/> 下載 + <see cref="IHistoricalKlineStore"/> SQLite 快取。
/// 多執行緒時每個 worker 自己 CreateScope 拿到獨立 <c>AppDbContext</c>。
/// </summary>
public static class BacktestRunner
{
    private static readonly Symbol DefaultSymbol = Symbol.Parse("BTC-USDT");
    private const KlineInterval DefaultInterval = KlineInterval.OneHour;
    private const string DefaultStrategyType = "SmaCrossover";
    private const int LookbackDays = 30;
    private const int DefaultWarmupBars = 120; // 蓋過最長 SlowSma=100 +餘裕
    private const decimal InitialBalance = 10_000m;

    /// <summary>排行榜最少成交次數；低於此數的組合視為無統計意義，過濾掉。</summary>
    private const int MinFillsForRanking = 3;

    public static async Task<int> RunAsync(IServiceProvider rootServices, string[] args, CancellationToken ct = default)
    {
        var optimize = args.Any(a => string.Equals(a, "--optimize", StringComparison.OrdinalIgnoreCase));

        // CAP-003 Phase 0a：解析 CLI args
        var symbol = ParseSymbolArg(args) ?? DefaultSymbol;
        var interval = ParseIntervalArg(args) ?? DefaultInterval;
        var strategyType = ParseStrategyArg(args, rootServices) ?? DefaultStrategyType;
        var overrideParams = ParseParamsArg(args);

        // 1) 先確保資料在 SQLite（單執行緒，避免下載期間撞 rate limit）
        var (start, end) = await EnsureHistoricalDataAsync(rootServices, symbol, interval, ct).ConfigureAwait(false);

        // 2) 分支
        return optimize
            ? await RunOptimizationAsync(rootServices, symbol, interval, start, end, ct).ConfigureAwait(false)
            : await RunSingleAsync(rootServices, symbol, interval, strategyType, overrideParams, start, end, ct).ConfigureAwait(false);
    }

    // ================================================================
    // Args 解析（CAP-003 Phase 0a）
    // ================================================================

    /// <summary>取 <c>--&lt;name&gt; &lt;value&gt;</c> 形式的下一個 token，沒帶或在結尾則回 null。</summary>
    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static Symbol? ParseSymbolArg(string[] args)
    {
        var raw = GetArgValue(args, "--symbol");
        return string.IsNullOrWhiteSpace(raw) ? null : Symbol.Parse(raw);
    }

    private static KlineInterval? ParseIntervalArg(string[] args)
    {
        var raw = GetArgValue(args, "--interval");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1m" => KlineInterval.OneMinute,
            "3m" => KlineInterval.ThreeMinutes,
            "5m" => KlineInterval.FiveMinutes,
            "15m" => KlineInterval.FifteenMinutes,
            "30m" => KlineInterval.ThirtyMinutes,
            "1h" => KlineInterval.OneHour,
            "2h" => KlineInterval.TwoHours,
            "4h" => KlineInterval.FourHours,
            "8h" => KlineInterval.EightHours,
            "12h" => KlineInterval.TwelveHours,
            "1d" => KlineInterval.OneDay,
            _ => throw new ArgumentException(
                $"Unknown --interval '{raw}'. Supported: 1m / 3m / 5m / 15m / 30m / 1h / 2h / 4h / 8h / 12h / 1d.")
        };
    }

    /// <summary>
    /// 解析 <c>--strategy</c>：先 case-insensitive 完整比對 KnownTypes；不中再 normalize（移 dash + ToLower）後比對。
    /// 既不阻擋 PascalCase（"TrendFollowing"），也容忍 kebab-case（"trend-following"）。
    /// </summary>
    private static string? ParseStrategyArg(string[] args, IServiceProvider rootServices)
    {
        var raw = GetArgValue(args, "--strategy");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        using var scope = rootServices.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();
        var known = factory.KnownTypes;

        // Pass 1: 大小寫不敏感完全匹配
        foreach (var k in known)
        {
            if (string.Equals(k, raw, StringComparison.OrdinalIgnoreCase))
                return k;
        }

        // Pass 2: 移除 dash 後不分大小寫匹配（trend-following → TrendFollowing）
        var normalized = raw.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        foreach (var k in known)
        {
            if (string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase))
                return k;
        }

        throw new ArgumentException(
            $"Unknown --strategy '{raw}'. Known: [{string.Join(", ", known)}].");
    }

    /// <summary>
    /// 解析 <c>--params "FastEmaPeriod=7,SlowEmaPeriod=45,RsiPeriod=17,RsiMidline=45"</c>。
    /// 值以 InvariantCulture 解析為 decimal — 避免 zh-TW locale 把 "." 當千位分隔。
    /// </summary>
    private static IReadOnlyDictionary<string, decimal>? ParseParamsArg(string[] args)
    {
        var raw = GetArgValue(args, "--params");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || eq == pair.Length - 1)
                throw new ArgumentException($"Bad --params entry '{pair}'. Expect 'Key=Value'.");
            var key = pair[..eq].Trim();
            var valStr = pair[(eq + 1)..].Trim();
            if (!decimal.TryParse(valStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
                throw new ArgumentException($"Bad --params value for '{key}': '{valStr}' not a decimal.");
            map[key] = val;
        }
        return map.Count == 0 ? null : map;
    }

    // ================================================================
    // 下載階段
    // ================================================================
    private static async Task<(DateTime start, DateTime end)> EnsureHistoricalDataAsync(
        IServiceProvider rootServices, Symbol symbol, KlineInterval interval, CancellationToken ct)
    {
        using var scope = rootServices.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IHistoricalDataProvider>();
        var store = scope.ServiceProvider.GetRequiredService<IHistoricalKlineStore>();

        var end = DateTime.UtcNow;
        var start = end.AddDays(-LookbackDays);

        Console.WriteLine();
        Console.WriteLine("=====================================================");
        Console.WriteLine($"  CryptoBot Backtest — {symbol.BingXFormat} {interval}, last {LookbackDays} days");
        Console.WriteLine($"  range: {start:yyyy-MM-dd HH:mm} → {end:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine("=====================================================");

        var totalDownloaded = 0;
        await foreach (var batch in provider.DownloadAsync(symbol, interval, start, end, ct).ConfigureAwait(false))
        {
            await store.UpsertAsync(symbol, interval, batch, ct).ConfigureAwait(false);
            totalDownloaded += batch.Count;
        }
        Console.WriteLine($"📥 Downloaded + stored {totalDownloaded} klines.");

        var storedCount = await store.CountRangeAsync(symbol, interval, start, end, ct).ConfigureAwait(false);
        Console.WriteLine($"💾 SQLite store now contains {storedCount} klines in range.");

        return (start, end);
    }

    // ================================================================
    // 單次回測
    // ================================================================
    private static async Task<int> RunSingleAsync(
        IServiceProvider rootServices,
        Symbol symbol,
        KlineInterval interval,
        string strategyType,
        IReadOnlyDictionary<string, decimal>? overrideParams,
        DateTime start, DateTime end, CancellationToken ct)
    {
        var options = BuildOptions(symbol, interval, start, end, DefaultWarmupBars);
        var parameters = overrideParams ?? DefaultSmaParameters();
        var config = BuildConfigFromParameters(symbol, interval, parameters, DefaultWarmupBars);

        using var scope = rootServices.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();
        var strategy = factory.Get(strategyType);

        Console.WriteLine($"  Strategy                : {strategyType}");
        Console.WriteLine($"  Parameters              : {{ {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value:N4}"))} }}");

        var report = await RunOnceAsync(scope.ServiceProvider, options, config, strategy, ct).ConfigureAwait(false);

        PrintReport(report);
        return 0;
    }

    // ================================================================
    // 參數優化（SmaCrossover 笛卡兒積 — 目前 optimize 路徑仍特定於 SMA；
    //          symbol/interval 已可由 --symbol/--interval 覆寫）
    // ================================================================
    private static async Task<int> RunOptimizationAsync(
        IServiceProvider rootServices, Symbol symbol, KlineInterval interval,
        DateTime start, DateTime end, CancellationToken ct)
    {
        var loggerFactory = rootServices.GetRequiredService<ILoggerFactory>();
        var optimizer = new StrategyOptimizer(loggerFactory.CreateLogger<StrategyOptimizer>());

        // 想調整掃描範圍：改這裡就好。
        // 例：FastSma 5→15 step 1（11 個值）× SlowSma 40→60 step 2（11 個值）= 121 組合；
        //     過濾掉 Fast >= Slow 之後剩有效組合送進回測。
        // 降噪掃描：1h 週期 × 30 天窗口，Fast 5~20 / Slow 30~100。
        // Fast 16 個值 × Slow 15 個值 = 240 組合；過濾 Fast >= Slow 之後剩有效組合。
        var ranges = new[]
        {
            new ParameterRange("FastSmaPeriod", Min:  5, Max:  20, Step: 1),
            new ParameterRange("SlowSmaPeriod", Min: 30, Max: 100, Step: 5),
        };

        var runs = await optimizer.RunAsync(
            ranges,
            runOne: async (paramSet, token) =>
            {
                var fast = (int)paramSet["FastSmaPeriod"];
                var slow = (int)paramSet["SlowSmaPeriod"];
                if (fast >= slow)
                {
                    // 無效組合：快線沒比慢線快 — 直接回零損益、零成交的空報告，不浪費算力
                    return new BacktestReport(
                        TotalKlines: 0, SignalsTriggered: 0, OrdersFilled: 0,
                        StartingBalance: InitialBalance, EndingBalance: InitialBalance,
                        PeakEquity: InitialBalance, MaxDrawdownPercent: 0m,
                        FirstKlineTime: null, LastKlineTime: null,
                        Fills: Array.Empty<CryptoBot.Domain.Aggregates.OrderAggregate.Order>(),
                        EquityCurve: Array.Empty<EquityPoint>());
                }

                var options = BuildOptions(symbol, interval, start, end, DefaultWarmupBars);
                var config = BuildConfigFromParameters(symbol, interval, new Dictionary<string, decimal>
                {
                    ["FastSmaPeriod"] = fast,
                    ["SlowSmaPeriod"] = slow,
                }, DefaultWarmupBars);

                // 關鍵：每組參數都開自己的 DI scope，才能各自拿到乾淨的 AppDbContext / Store
                using var scope = rootServices.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<IStrategyFactory>();
                var strategy = factory.Get(DefaultStrategyType); // SmaCrossover
                return await RunOnceAsync(scope.ServiceProvider, options, config, strategy, token).ConfigureAwait(false);
            },
            ct: ct).ConfigureAwait(false);

        PrintLeaderboard(runs);
        return 0;
    }

    // ================================================================
    // 單組回測執行（DI scope 已由呼叫端建好）
    // ================================================================
    private static async Task<BacktestReport> RunOnceAsync(
        IServiceProvider scopedServices,
        BacktestOptions options,
        StrategyConfiguration config,
        IStrategy strategy,
        CancellationToken ct)
    {
        var store = scopedServices.GetRequiredService<IHistoricalKlineStore>();
        var loggerFactory = scopedServices.GetRequiredService<ILoggerFactory>();

        var simulator = new BacktestSimulator(options, loggerFactory.CreateLogger<BacktestSimulator>());

        var engine = new BacktestEngine(
            store: store,
            clock: simulator,
            exchange: simulator,
            strategy: strategy,
            logger: loggerFactory.CreateLogger<BacktestEngine>());

        return await engine.RunAsync(options, config, Guid.NewGuid(), ct).ConfigureAwait(false);
    }

    private static BacktestOptions BuildOptions(Symbol symbol, KlineInterval interval, DateTime start, DateTime end, int warmup) => new()
    {
        Symbol = symbol.BingXFormat,
        Interval = interval,
        StartTime = start,
        EndTime = end,
        InitialBalance = InitialBalance,
        SlippageBps = 5m,
        CommissionRate = 0.0005m,
        WarmupBars = warmup,
    };

    private static StrategyConfiguration BuildConfigFromParameters(
        Symbol symbol, KlineInterval interval,
        IReadOnlyDictionary<string, decimal> parameters, int warmupBars) =>
        StrategyConfiguration.Create(
            symbol: symbol,
            interval: interval,
            leverage: Leverage.Conservative,
            maxKlineWindow: warmupBars,
            parameters: parameters);

    private static IReadOnlyDictionary<string, decimal> DefaultSmaParameters() => new Dictionary<string, decimal>
    {
        ["FastSmaPeriod"] = 20m,
        ["SlowSmaPeriod"] = 50m,
    };

    // ================================================================
    // 輸出
    // ================================================================
    private static void PrintReport(BacktestReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=====================================================");
        Console.WriteLine("                 BACKTEST REPORT");
        Console.WriteLine("=====================================================");
        Console.WriteLine($"  Klines replayed         : {report.TotalKlines}");
        Console.WriteLine($"  Window                  : {report.FirstKlineTime:yyyy-MM-dd HH:mm} → {report.LastKlineTime:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  Signals triggered       : {report.SignalsTriggered}");
        Console.WriteLine($"  Orders filled           : {report.OrdersFilled}");
        Console.WriteLine($"  Starting balance (USDT) : {report.StartingBalance,14:N4}");
        Console.WriteLine($"  Ending   balance (USDT) : {report.EndingBalance,14:N4}");
        Console.WriteLine($"  Net P&L          (USDT) : {report.NetPnL,14:N4}");
        Console.WriteLine($"  Return                  : {report.ReturnPercent,13:N2} %");
        Console.WriteLine($"  Peak equity      (USDT) : {report.PeakEquity,14:N4}");
        Console.WriteLine($"  Max drawdown            : {report.MaxDrawdownPercent,13:N2} %");
        Console.WriteLine($"  Sharpe ratio (annualized): {report.SharpeRatio,13:N4}");
        Console.WriteLine("=====================================================");
        Console.WriteLine();
    }

    private static void PrintLeaderboard(IReadOnlyList<OptimizationRun> runs)
    {
        // 1) 先過濾掉成交次數太少的組合（統計噪音 / 策略幾乎沒觸發）。
        // 2) 再用 Profit/MaxDD 比率（單位回撤換取的獲利）由高到低排序 — 追求最「穩健」而非最「肥」的參數。
        //    MaxDD == 0 的罕見情況：有獲利 → 視為無限大（排最前）；無獲利 → 比率為 0（排最後）。
        var ranked = runs
            .Where(r => r.Report.OrdersFilled >= MinFillsForRanking)
            .OrderByDescending(r => ProfitToDrawdownRatio(r.Report))
            .ToList();

        var filteredOut = runs.Count - ranked.Count;

        Console.WriteLine();
        Console.WriteLine("=========================================================================================");
        Console.WriteLine("                          PARAMETER OPTIMIZATION LEADERBOARD");
        Console.WriteLine($"  total runs = {runs.Count}   shown = {ranked.Count}   filtered (Fills < {MinFillsForRanking}) = {filteredOut}");
        Console.WriteLine("  ranked by Profit / MaxDrawdown ratio (higher = more robust), descending");
        Console.WriteLine("=========================================================================================");
        Console.WriteLine($"  {"#",-4}{"Fast",-6}{"Slow",-6}{"PnL (USDT)",-14}{"Return",-12}{"MaxDD",-12}{"P/DD",-10}{"Fills"}");
        Console.WriteLine("  ---------------------------------------------------------------------------------------");

        var rank = 1;
        foreach (var run in ranked)
        {
            var fast = (int)run.GetParameter("FastSmaPeriod");
            var slow = (int)run.GetParameter("SlowSmaPeriod");
            var r = run.Report;

            // 百分比格式：先把數字 + "%" 拼成字串再對齊，避免 %0.46 這種把符號甩到前面的 bug。
            var returnStr = $"{r.ReturnPercent:N2}%";
            var ddStr = $"{r.MaxDrawdownPercent:N2}%";
            var ratio = ProfitToDrawdownRatio(r);
            var ratioStr = ratio == decimal.MaxValue ? "∞" : $"{ratio:N2}";

            var line = $"  {rank++,-4}{fast,-6}{slow,-6}{r.NetPnL,-14:N4}{returnStr,-12}{ddStr,-12}{ratioStr,-10}{r.OrdersFilled}";
            Console.WriteLine(line);
        }

        if (ranked.Count == 0)
        {
            Console.WriteLine($"  (no runs passed the Fills >= {MinFillsForRanking} filter — try widening the parameter grid or the lookback window)");
        }

        Console.WriteLine("=========================================================================================");
        Console.WriteLine();
    }

    /// <summary>
    /// 穩健度指標：每 1% 最大回撤換來的淨利 USDT。
    /// MaxDD=0 代表整個回測沒發生過回撤 — 有獲利視為理想（+∞），沒獲利視為 0。
    /// </summary>
    private static decimal ProfitToDrawdownRatio(BacktestReport r)
    {
        if (r.MaxDrawdownPercent <= 0m)
            return r.NetPnL > 0m ? decimal.MaxValue : 0m;
        return r.NetPnL / r.MaxDrawdownPercent;
    }
}
