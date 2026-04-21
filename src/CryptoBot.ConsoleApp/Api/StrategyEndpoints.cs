using CryptoBot.Application.Strategies;
using CryptoBot.ConsoleApp.Api.Dtos;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/strategies/*</c> Minimal API 群組。
///
/// <list type="bullet">
///   <item><c>GET  /api/strategies</c> — 所有策略清單（含已停 / 已錯）</item>
///   <item><c>GET  /api/strategies/available-types</c> — S25 下拉選單：可用的策略類型字串</item>
///   <item><c>POST /api/strategies/{id}/toggle</c> — Running ↔ Stopped 遠端熱切換</item>
///   <item><c>PUT  /api/strategies/{id}/type</c> — S25 即時切換策略類型（含熱掛載）</item>
/// </list>
/// Toggle 與 ChangeType 都委託給 <see cref="IStrategyRuntimeController"/>，
/// 才能在不重啟 host 的狀況下真的新增 / 移除 / 重建背景 executor。
/// </summary>
public static class StrategyEndpoints
{
    public static IEndpointRouteBuilder MapStrategyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/strategies").WithTags("Strategies");

        group.MapGet("/", async (IStrategyRepository repo, CancellationToken ct) =>
        {
            var list = await repo.GetAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(list.Select(ToDto).ToList());
        });

        group.MapGet("/available-types", (IStrategyFactory factory) =>
            Results.Ok(factory.KnownTypes));

        group.MapPost("/{id:guid}/toggle", async (
            Guid id,
            IStrategyRepository repo,
            IStrategyRuntimeController controller,
            CancellationToken ct) =>
        {
            var strategy = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (strategy is null)
                return Results.NotFound(new { error = $"Strategy {id} not found." });

            // 以 DB 裡的狀態為準 — Running → 停；Stopped/Paused/Error → 啟。
            if (strategy.Status == StrategyStatus.Running)
            {
                var ok = await controller.StopAsync(id, ct).ConfigureAwait(false);
                return ok
                    ? Results.Ok(new ToggleResponseDto(id, "Stopped", $"Strategy {strategy.Name} stopped."))
                    : Results.Problem($"Failed to stop strategy {id}.", statusCode: 500);
            }
            else
            {
                var ok = await controller.StartAsync(id, ct).ConfigureAwait(false);
                return ok
                    ? Results.Ok(new ToggleResponseDto(id, "Running", $"Strategy {strategy.Name} started."))
                    : Results.Problem($"Failed to start strategy {id}.", statusCode: 500);
            }
        });

        group.MapPut("/{id:guid}/type", async (
            Guid id,
            ChangeStrategyTypeRequest body,
            IStrategyRepository repo,
            IStrategyFactory factory,
            IStrategyRuntimeController controller,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.StrategyType))
                return Results.BadRequest(new { error = "StrategyType is required." });

            if (!factory.KnownTypes.Contains(body.StrategyType))
                return Results.BadRequest(new
                {
                    error = $"Unknown strategy type '{body.StrategyType}'.",
                    known = factory.KnownTypes,
                });

            var strategy = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (strategy is null)
                return Results.NotFound(new { error = $"Strategy {id} not found." });

            var ok = await controller.ChangeStrategyTypeAsync(id, body.StrategyType, ct).ConfigureAwait(false);
            if (!ok)
                return Results.Problem(
                    $"Failed to change strategy {id} type to {body.StrategyType}.",
                    statusCode: 500);

            // 重拉一次拿最新狀態（ChangeType 可能決定是否 resume Running）
            var updated = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new ChangeStrategyTypeResponseDto(
                Id: id,
                StrategyType: updated?.StrategyType ?? body.StrategyType,
                Status: updated?.Status.ToString() ?? "Unknown",
                Message: $"Strategy {strategy.Name} switched to {body.StrategyType}."));
        });

        return app;
    }

    internal static StrategyDto ToDto(Strategy s) => new(
        Id: s.Id,
        Name: s.Name,
        StrategyType: s.StrategyType,
        Status: s.Status.ToString(),
        Symbol: s.Configuration.Symbol.BingXFormat,
        Interval: s.Configuration.Interval.ToString(),
        Leverage: s.Configuration.Leverage.Value,
        RiskPerTradePercent: s.Configuration.RiskPerTradePercent,
        StopLossPercent: s.Configuration.StopLossPercent,
        TakeProfitPercent: s.Configuration.TakeProfitPercent,
        MaxKlineWindow: s.Configuration.MaxKlineWindow,
        Parameters: s.Configuration.Parameters,
        TotalTrades: s.TotalTrades,
        WinningTrades: s.WinningTrades,
        LosingTrades: s.LosingTrades,
        WinRate: s.WinRate,
        CumulativePnL: s.CumulativePnL,
        CreatedAt: s.CreatedAt,
        StartedAt: s.StartedAt,
        StoppedAt: s.StoppedAt,
        LastError: s.LastError);
}
