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
///   <item><c>POST /api/strategies/{id}/toggle</c> — Running ↔ Stopped 遠端熱切換</item>
/// </list>
/// Toggle 委託給 <see cref="IStrategyRuntimeController"/>，才能在不重啟 host 的狀況下
/// 真的新增 / 移除背景 executor。
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
