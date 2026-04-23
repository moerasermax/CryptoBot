using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.ConsoleApp.Api.Dtos;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
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

        group.MapGet("/", async (
            IStrategyRepository repo,
            IStrategyRuntimeController controller,
            CancellationToken ct) =>
        {
            var list = await repo.GetAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(list
                .Select(s => ToDto(s, controller.GetLastEvaluatedAtUtc(s.Id)))
                .ToList());
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

        // S28 T1：熔斷狀態查詢 — UI 初始化與每次操作後會呼叫此端點判斷是否鎖住 toggle。
        group.MapGet("/breaker", (ISafetyBreakerState breaker) =>
            Results.Ok(new BreakerStatusDto(
                IsTripped: breaker.IsTripped,
                TrippedAtUtc: breaker.TrippedAtUtc,
                Reason: breaker.Reason)));

        // S28 T1：管理員手動解除熔斷標記 — 解除後 UI 就能重新啟動策略。
        // 有意不做授權檢查（S28 尚未導入 identity），只靠 IP 白名單限制 — 日後接 S29 auth 再加。
        group.MapPost("/breaker/reset", (ISafetyBreakerState breaker) =>
        {
            var did = breaker.Reset("manual admin reset via API");
            return Results.Ok(new BreakerResetResponseDto(
                Reset: did,
                Message: did ? "Breaker reset — strategies can be started again."
                              : "Breaker was not tripped; no action taken."));
        });

        // S28 T2：緊急停機 — 撤單 + 平倉 + 停所有策略。任何步驟失敗都繼續下一步，
        // 以錯誤清單回報給 UI（最怕的不是「有些步驟失敗」，是「整個流程卡住策略還在跑」）。
        group.MapPost("/kill-switch", async (
            IOrderRepository orderRepo,
            IPositionRepository positionRepo,
            IExchangeClient exchange,
            IUnitOfWork uow,
            IStrategyRuntimeController controller,
            INotificationService notifications,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("KillSwitchEndpoint");
            logger.LogCritical("🚨 EMERGENCY KILL SWITCH triggered via API.");
            var errors = new List<string>();
            int ordersCancelled = 0;
            int positionsClosed = 0;

            // 1) 撤銷所有 active orders
            var active = await orderRepo.GetActiveOrdersAsync(ct).ConfigureAwait(false);
            foreach (var order in active)
            {
                try
                {
                    await exchange.CancelOrderAsync(order, ct).ConfigureAwait(false);
                    await orderRepo.UpdateAsync(order, ct).ConfigureAwait(false);
                    ordersCancelled++;
                }
                catch (Exception ex)
                {
                    var msg = $"Cancel order {order.Id} failed: {ex.Message}";
                    logger.LogError(ex, "Kill switch: {Msg}", msg);
                    errors.Add(msg);
                }
            }

            // 2) 市價平掉所有持倉
            var positions = await positionRepo.GetOpenPositionsAsync(ct).ConfigureAwait(false);
            foreach (var position in positions)
            {
                try
                {
                    var closeSide = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
                    var closeOrder = Order.CreateMarketOrder(
                        position.Symbol, closeSide, position.Side, position.Quantity, position.StrategyId);

                    await orderRepo.AddAsync(closeOrder, ct).ConfigureAwait(false);
                    await exchange.PlaceOrderAsync(closeOrder, ct).ConfigureAwait(false);
                    await orderRepo.UpdateAsync(closeOrder, ct).ConfigureAwait(false);
                    positionsClosed++;
                }
                catch (Exception ex)
                {
                    var msg = $"Close position {position.Id} ({position.Symbol}) failed: {ex.Message}";
                    logger.LogError(ex, "Kill switch: {Msg}", msg);
                    errors.Add(msg);
                }
            }

            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            // 3) 停掉所有策略（DB 狀態一併翻為 Stopped）
            IReadOnlyList<Guid> stopped;
            try
            {
                stopped = await controller.StopAllAsync(
                    "Emergency kill switch activated by operator", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Kill switch: StopAllAsync threw.");
                errors.Add($"StopAll failed: {ex.Message}");
                stopped = Array.Empty<Guid>();
            }

            // 4) 發 Critical 通知 — 不管成功失敗都要讓值班人員知道發生了 kill switch
            try
            {
                await notifications.NotifyAsync(
                    "🚨 Emergency Kill Switch",
                    $"Orders cancelled: {ordersCancelled}. Positions closed: {positionsClosed}. " +
                    $"Strategies stopped: {stopped.Count}. Errors: {errors.Count}.",
                    NotificationLevel.Critical,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Kill switch notification dispatch failed.");
            }

            var message = errors.Count == 0
                ? "Kill switch executed cleanly."
                : $"Kill switch executed with {errors.Count} partial failure(s) — check errors.";

            return Results.Ok(new KillSwitchResponseDto(
                OrdersCancelled: ordersCancelled,
                PositionsClosed: positionsClosed,
                StrategiesStopped: stopped.Count,
                Errors: errors,
                Message: message));
        });

        return app;
    }

    internal static StrategyDto ToDto(Strategy s, DateTime? lastEvaluatedAtUtc = null) => new(
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
        LastError: s.LastError,
        LastEvaluatedAtUtc: lastEvaluatedAtUtc);
}
