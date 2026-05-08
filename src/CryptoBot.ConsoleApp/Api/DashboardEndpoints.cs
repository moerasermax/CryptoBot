using CryptoBot.ConsoleApp.Api.Dtos;
using CryptoBot.ConsoleApp.Services;
using CryptoBot.Domain.Repositories;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/dashboard/*</c> Minimal API 群組。
///
/// <list type="bullet">
///   <item><c>GET /api/dashboard/stats</c> — 三卡總覽 + 目前開倉 + 最近 10 筆訂單</item>
/// </list>
/// Blazor 頁面首次載入時拉一次 snapshot（不用等 SignalR 心跳）。
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard");

        group.MapGet("/stats", async (
            DashboardStatsService stats,
            IPositionRepository positionRepo,
            IOrderRepository orderRepo,
            CancellationToken ct) =>
        {
            var s = await stats.GetAsync(ct).ConfigureAwait(false);

            var openPositions = await positionRepo.GetOpenPositionsAsync(ct).ConfigureAwait(false);
            var positionDtos = openPositions.Select(p => new OpenPositionDto(
                Id: p.Id,
                Symbol: p.Symbol.BingXFormat,
                Side: p.Side.ToString(),
                Quantity: p.Quantity.Value,
                EntryPrice: p.EntryPrice.Value,
                CurrentPrice: p.CurrentPrice?.Value,
                UnrealizedPnL: p.UnrealizedPnL,
                UnrealizedPnLPercent: p.UnrealizedPnLPercent,
                OpenedAt: p.OpenedAt)).ToList();

            var recentOrders = await orderRepo.GetRecentAsync(10, ct).ConfigureAwait(false);
            var recent = recentOrders
                .Select(o => new RecentTradeDto(
                    OrderId: o.Id,
                    CreatedAt: o.CreatedAt,
                    Symbol: o.Symbol.BingXFormat,
                    Side: o.Side.ToString(),
                    PositionSide: o.PositionSide.ToString(),
                    Quantity: o.Quantity.Value,
                    AverageFillPrice: o.AverageFillPrice?.Value,
                    Status: o.Status.ToString(),
                    RejectReason: o.RejectReason,
                    ExchangeOrderId: o.ExchangeOrderId))
                .ToList();

            return Results.Ok(new DashboardStatsDto(
                TotalEquity: s.TotalEquity,
                TodayRealizedPnL: s.TodayRealizedPnL,
                OpenUnrealizedPnL: s.OpenUnrealizedPnL,
                ActiveStrategyCount: s.ActiveStrategyCount,
                OpenPositions: positionDtos,
                RecentTrades: recent));
        });

        // S39：完整交易歷史（依 ClosedAt 由新到舊） — 預設 50 筆，clamp 1..200。
        group.MapGet("/trade-history", async (
            IPositionRepository positionRepo,
            int? limit,
            CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 50, 1, 200);
            var closed = await positionRepo.GetRecentClosedAsync(n, ct).ConfigureAwait(false);
            var rows = closed.Select(p => new ClosedTradeDto(
                PositionId: p.Id,
                Symbol: p.Symbol.BingXFormat,
                Side: p.Side.ToString(),
                Quantity: p.Quantity.Value,
                EntryPrice: p.EntryPrice.Value,
                EntryTimeUtc: p.OpenedAt,
                ExitPrice: p.ExitPrice?.Value,
                ExitTimeUtc: p.ClosedAt,
                RealizedPnL: p.RealizedPnL,
                TotalCommission: p.TotalCommission,
                LeverageValue: p.Leverage.Value,
                StrategyType: p.StrategyType,
                ParametersSnapshot: p.ParametersSnapshot)).ToList();
            return Results.Ok(rows);
        });

        return app;
    }
}
