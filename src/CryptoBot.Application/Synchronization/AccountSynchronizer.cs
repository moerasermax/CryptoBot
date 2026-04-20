using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// <see cref="IAccountSynchronizer"/> 的預設實作。
///
/// DI 生命週期：Singleton（掛 WS handler 需要持久對象）。
/// 每次收到事件內部會開一個 Scoped DI scope 拿 repository + UnitOfWork。
///
/// 設計要點：
/// - <see cref="StartAsync"/> 幂等 — 使用 bool 防重掛 handler。
/// - 所有對 Order aggregate 的狀態改動都必須配合 <see cref="IUnitOfWork.SaveChangesAsync"/>
///   寫回 DB；否則 WS 事件白處理。
/// - Position 平倉需要退出價：WS 事件本身沒提供，我們 on-demand 從 <see cref="IExchangeClient.GetMarkPriceAsync"/>
///   取當下 MarkPrice 當作近似退出價。
/// - 所有 handler 內部的例外都 log 後吞掉 — WS 執行緒一路上拋會導致 SDK 連線被拖垮。
/// </summary>
public sealed class AccountSynchronizer : IAccountSynchronizer
{
    private readonly IMarketDataStream _marketData;
    private readonly IExchangeClient _exchange;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountSynchronizer> _logger;

    private Func<ExchangeOrderUpdate, Task>? _orderHandler;
    private Func<ExchangeAccountUpdate, Task>? _accountHandler;
    private bool _running;

    public AccountSynchronizer(
        IMarketDataStream marketData,
        IExchangeClient exchange,
        IServiceScopeFactory scopeFactory,
        ILogger<AccountSynchronizer> logger)
    {
        _marketData = marketData;
        _exchange = exchange;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_running) return Task.CompletedTask;

        _orderHandler = HandleOrderUpdateAsync;
        _accountHandler = HandleAccountUpdateAsync;
        _marketData.OnExchangeOrderUpdate += _orderHandler;
        _marketData.OnExchangeAccountUpdate += _accountHandler;
        _running = true;

        _logger.LogInformation("AccountSynchronizer subscribed to exchange user-data stream.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_running) return Task.CompletedTask;

        if (_orderHandler is not null)
        {
            _marketData.OnExchangeOrderUpdate -= _orderHandler;
            _orderHandler = null;
        }
        if (_accountHandler is not null)
        {
            _marketData.OnExchangeAccountUpdate -= _accountHandler;
            _accountHandler = null;
        }
        _running = false;

        _logger.LogInformation("AccountSynchronizer unsubscribed.");
        return Task.CompletedTask;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("AccountSynchronizer starting reconciliation…");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var orderRepo = sp.GetRequiredService<IOrderRepository>();
        var positionRepo = sp.GetRequiredService<IPositionRepository>();
        var uow = sp.GetRequiredService<IUnitOfWork>();

        // 1) 訂單對帳 — 把每筆尚未終結的本地訂單都打 REST 更新一次
        var activeOrders = await orderRepo.GetActiveOrdersAsync(ct).ConfigureAwait(false);
        var refreshed = 0;
        foreach (var order in activeOrders)
        {
            if (string.IsNullOrEmpty(order.ExchangeOrderId))
            {
                _logger.LogWarning(
                    "Reconcile skip: local order {Id} has no ExchangeOrderId (likely never reached exchange).",
                    order.Id);
                continue;
            }

            try
            {
                await _exchange.RefreshOrderStatusAsync(order, ct).ConfigureAwait(false);
                refreshed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconcile: refresh failed for order {Id} (exchangeId={ExId}).",
                    order.Id, order.ExchangeOrderId);
            }
        }

        // 2) 倉位對帳 — 本地 open 但遠端已不存在 ⇒ 以 MarkPrice 平倉
        var localOpen = await positionRepo.GetOpenPositionsAsync(ct).ConfigureAwait(false);
        var remoteOpen = await _exchange.GetOpenPositionsAsync(ct).ConfigureAwait(false);

        var closedOrphans = 0;
        foreach (var local in localOpen)
        {
            var matched = remoteOpen.Any(r =>
                r.Symbol.Equals(local.Symbol) &&
                r.Side == local.Side &&
                r.Quantity > 0m);
            if (matched) continue;

            try
            {
                var mark = await _exchange.GetMarkPriceAsync(local.Symbol, ct).ConfigureAwait(false);
                local.Close(mark, reason: "Reconcile: position not found on exchange");
                await positionRepo.UpdateAsync(local, ct).ConfigureAwait(false);
                closedOrphans++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconcile: failed to close orphan local position {Id} ({Symbol} {Side}).",
                    local.Id, local.Symbol, local.Side);
            }
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Reconciliation complete: refreshed {Orders} orders, closed {Orphans} orphan positions.",
            refreshed, closedOrphans);
    }

    // ========== WS event handlers ==========

    private async Task HandleOrderUpdateAsync(ExchangeOrderUpdate update)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var orderRepo = sp.GetRequiredService<IOrderRepository>();
            var uow = sp.GetRequiredService<IUnitOfWork>();

            var order = await orderRepo.GetByExchangeOrderIdAsync(update.ExchangeOrderId, CancellationToken.None)
                .ConfigureAwait(false);
            if (order is null)
            {
                _logger.LogDebug(
                    "Order update for unknown exchange order {ExId} ({Symbol} {Status}) — ignoring.",
                    update.ExchangeOrderId, update.Symbol, update.Status);
                return;
            }

            ApplyOrderStateTransition(order, update);
            await orderRepo.UpdateAsync(order, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Order {ExId} → {Status} (filled={Filled}/{Qty}, avg={Avg}).",
                update.ExchangeOrderId, update.Status,
                update.QuantityFilled, update.Quantity, update.AverageFillPrice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AccountSynchronizer: error handling order update {ExId}.",
                update.ExchangeOrderId);
        }
    }

    private async Task HandleAccountUpdateAsync(ExchangeAccountUpdate update)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var positionRepo = sp.GetRequiredService<IPositionRepository>();
            var uow = sp.GetRequiredService<IUnitOfWork>();

            foreach (var remote in update.Positions)
            {
                var openLocal = await positionRepo
                    .GetOpenPositionsBySymbolAsync(remote.Symbol, CancellationToken.None)
                    .ConfigureAwait(false);
                var match = openLocal.FirstOrDefault(p => p.Side == remote.Side);
                if (match is null) continue;

                if (remote.Quantity == 0m)
                {
                    var exitPrice = remote.MarkPrice > 0m
                        ? Price.Create(remote.MarkPrice)
                        : await _exchange.GetMarkPriceAsync(remote.Symbol, CancellationToken.None)
                            .ConfigureAwait(false);
                    match.Close(exitPrice, reason: "Exchange reported position closed");
                    await positionRepo.UpdateAsync(match, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Position {Id} auto-closed via WS ({Symbol} {Side} @ {Price}).",
                        match.Id, match.Symbol, match.Side, exitPrice.Value);
                }
                else if (remote.MarkPrice > 0m)
                {
                    match.UpdateCurrentPrice(Price.Create(remote.MarkPrice));
                    await positionRepo.UpdateAsync(match, CancellationToken.None).ConfigureAwait(false);
                }
            }

            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AccountSynchronizer: error handling account update.");
        }
    }

    private static void ApplyOrderStateTransition(Order order, ExchangeOrderUpdate update)
    {
        // 成交進度：如果遠端已成交量 > 本地，補齊差額
        var localFilled = order.FilledQuantity.Value;
        if (update.QuantityFilled > localFilled &&
            update.AverageFillPrice is > 0m)
        {
            var delta = update.QuantityFilled - localFilled;
            order.RecordFill(
                filledQty: Quantity.Create(delta),
                fillPrice: Price.Create(update.AverageFillPrice.Value),
                commission: update.Fee);
        }

        if (!order.IsActive) return;

        switch (update.Status)
        {
            case OrderStatus.Canceled: order.Cancel("Canceled on exchange"); break;
            case OrderStatus.Rejected: order.Reject("Rejected on exchange"); break;
            case OrderStatus.Expired:  order.Expire(); break;
        }
    }
}
