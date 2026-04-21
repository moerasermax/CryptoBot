using CryptoBot.Application.Strategies;
using CryptoBot.ConsoleApp.Services;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;

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
    /// <summary>整個優化工作的組合總數防呆上限 — 避免誤輸爆 rate limit / 記憶體。</summary>
    private const long MaxTotalCombinations = 1000;

    public static IEndpointRouteBuilder MapLabEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lab").WithTags("Lab");

        group.MapGet("/status", (OptimizationOrchestrator orchestrator) =>
            Results.Ok(new { isRunning = orchestrator.IsRunning }));

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
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (body.Parameters is null || body.Parameters.Count == 0)
                return Results.BadRequest(new { error = "Parameters must not be empty." });

            var strategy = await repo.GetByIdAsync(strategyId, ct).ConfigureAwait(false);
            if (strategy is null)
                return Results.NotFound(new { error = $"Strategy {strategyId} not found." });

            // 熱套用：Stop → UpdateConfiguration → Save → Start。
            // 這樣背景 executor 才會拿到新 config 重建。
            var wasRunning = strategy.Status == StrategyStatus.Running;
            if (wasRunning)
            {
                await controller.StopAsync(strategyId, ct).ConfigureAwait(false);
                // reload — 因為 StopAsync 會自己寫一次 DB
                strategy = await repo.GetByIdAsync(strategyId, ct).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("Strategy vanished after stop.");
            }

            // 保留原有未被此次套用覆寫的參數 — 字典合併而不是整個替換。
            var newParams = new Dictionary<string, decimal>(strategy.Configuration.Parameters);
            foreach (var kv in body.Parameters)
                newParams[kv.Key] = kv.Value;

            var current = strategy.Configuration;
            var newConfig = StrategyConfiguration.Create(
                symbol: current.Symbol,
                interval: current.Interval,
                leverage: current.Leverage,
                riskPerTradePercent: current.RiskPerTradePercent,
                stopLossPercent: current.StopLossPercent,
                takeProfitPercent: current.TakeProfitPercent,
                trailingStopPercent: current.TrailingStopPercent,
                maxConcurrentPositions: current.MaxConcurrentPositions,
                cooldownPeriod: current.CooldownPeriod,
                maxKlineWindow: current.MaxKlineWindow,
                parameters: newParams);
            strategy.UpdateConfiguration(newConfig);
            await repo.UpdateAsync(strategy, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            if (wasRunning)
            {
                await controller.StartAsync(strategyId, ct).ConfigureAwait(false);
            }

            return Results.Ok(new
            {
                strategyId,
                applied = body.Parameters,
                restarted = wasRunning,
            });
        });

        return app;
    }

    private static bool ValidateRequest(OptimizationRequest r, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(r.StrategyKey)) { error = "StrategyKey is required."; return false; }
        if (r.Ranges is null || r.Ranges.Count == 0)  { error = "At least one parameter range required."; return false; }
        if (r.StartUtc >= r.EndUtc)                   { error = "Start must be before End."; return false; }

        long total = 1;
        foreach (var range in r.Ranges)
        {
            if (range.Step <= 0) { error = $"Step must be positive for '{range.Name}'."; return false; }
            if (range.Max < range.Min) { error = $"Max must be >= Min for '{range.Name}'."; return false; }
            var count = (int)Math.Floor((double)((range.Max - range.Min) / range.Step)) + 1;
            total *= count;
            if (total > MaxTotalCombinations)
            {
                error = $"Too many combinations: {total} > {MaxTotalCombinations}. Narrow the ranges.";
                return false;
            }
        }

        return true;
    }
}
