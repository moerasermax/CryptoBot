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
///   <item><c>POST /api/lab/apply/{strategyId}</c> — 把排行榜某一行的 Fast/Slow 熱套用到現有策略</item>
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
            if (body.Fast <= 0 || body.Slow <= 0 || body.Fast >= body.Slow)
                return Results.BadRequest(new { error = "Fast and Slow must be positive and Fast < Slow." });

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

            var newParams = new Dictionary<string, decimal>(strategy.Configuration.Parameters)
            {
                ["FastSmaPeriod"] = body.Fast,
                ["SlowSmaPeriod"] = body.Slow,
            };
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
                applied = new { body.Fast, body.Slow },
                restarted = wasRunning,
            });
        });

        return app;
    }

    private static bool ValidateRequest(OptimizationRequest r, out string error)
    {
        error = string.Empty;
        if (r.FastStep <= 0 || r.SlowStep <= 0) { error = "Step must be positive."; return false; }
        if (r.FastMax < r.FastMin || r.SlowMax < r.SlowMin) { error = "Max must be >= Min."; return false; }
        if (r.StartUtc >= r.EndUtc) { error = "Start must be before End."; return false; }

        // 防呆：總格數超過 1000 通常是誤輸，伺服器會爆記憶體 / rate limit
        var fastCount = (int)Math.Floor((double)((r.FastMax - r.FastMin) / r.FastStep)) + 1;
        var slowCount = (int)Math.Floor((double)((r.SlowMax - r.SlowMin) / r.SlowStep)) + 1;
        if ((long)fastCount * slowCount > 1000)
        {
            error = $"Too many combinations: {fastCount}×{slowCount} > 1000. Narrow the ranges.";
            return false;
        }

        return true;
    }
}
