using CryptoBot.Application.Common.DomainEvents;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Events;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Trading;

/// <summary>
/// S77 Bug 12：<see cref="OrderFilledEvent"/> handler — 確保 open-side Order Filled 後立即建 Position。
///
/// 取代既有設計缺陷 (Bug 3):
///   - 既有 StrategyExecutor.cs:531-549 catch 例外吞掉 → Position 沒建（持倉隱形）
///   - Reconcile 5min tick race condition 跟不上 strategy 快速開/關節奏 (Bug 11)
///
/// 本 handler 在 UnitOfWork.SaveChangesAsync 後立即執行 (event-driven):
///   1. Order 寫入 DB Filled 後立即 raise OrderFilledEvent
///   2. DomainEventDispatcher 找到本 handler 並 call HandleAsync
///   3. handler 檢查是否為 open-side (Buy+Long / Sell+Short)
///   4. 對應 DB Open Position 缺則立即建
///   5. handler 失敗只 log error (不向上傳遞、不擋 SaveChanges)
///
/// 設計考量:
///   - handler 寫 Position 用獨立 SaveChanges (避免遞迴 dispatch — 但 Position.Open raise PositionOpenedEvent
///     仍會走 dispatcher、若有對應 handler 注意 cycle)
///   - close-side Order Filled (Sell+Long / Buy+Short) 由 StrategyExecutor Bug 8 path 或 AccountSynchronizer 處理
/// </summary>
public sealed class OrderFilledPositionMaterializer : IDomainEventHandler<OrderFilledEvent>
{
    private readonly IOrderRepository _orderRepo;
    private readonly IPositionRepository _positionRepo;
    private readonly IStrategyRepository _strategyRepo;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<OrderFilledPositionMaterializer> _logger;

    public OrderFilledPositionMaterializer(
        IOrderRepository orderRepo,
        IPositionRepository positionRepo,
        IStrategyRepository strategyRepo,
        IUnitOfWork uow,
        ILogger<OrderFilledPositionMaterializer> logger)
    {
        _orderRepo = orderRepo;
        _positionRepo = positionRepo;
        _strategyRepo = strategyRepo;
        _uow = uow;
        _logger = logger;
    }

    public async Task HandleAsync(OrderFilledEvent evt, CancellationToken ct = default)
    {
        // 取 full Order entity (event 只帶 id + summary, 完整資料要從 repo)
        var order = await _orderRepo.GetByIdAsync(evt.OrderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            _logger.LogWarning("[ORDER_FILLED_HANDLER] Order {Id} not found in repo — skip materialization.", evt.OrderId);
            return;
        }

        if (order.Status != OrderStatus.Filled) return;
        if (order.AverageFillPrice is null) return;
        if (order.FilledQuantity.Value <= 0m) return;

        // 判斷是否為 open-side
        var openSide = (order.Side, order.PositionSide) switch
        {
            (OrderSide.Buy, PositionSide.Long) => (PositionSide?)PositionSide.Long,
            (OrderSide.Sell, PositionSide.Short) => PositionSide.Short,
            _ => null,
        };
        if (openSide is null) return; // close-side order, 不在本 handler 範圍

        // 對應 DB Open Position - 若已有則 skip (idempotent)
        var existing = await _positionRepo.GetOpenPositionsBySymbolAsync(order.Symbol, ct).ConfigureAwait(false);
        if (existing.Any(p => p.Side == openSide.Value && !p.IsClosed))
        {
            // StrategyExecutor materialization 已成功、不需補建
            return;
        }

        // 反查 Strategy 取 leverage + strategyType (fallback null → defaults)
        Strategy? strategy = null;
        Leverage leverage = Leverage.Conservative;
        string? strategyType = null;
        if (order.StrategyId.HasValue)
        {
            try
            {
                strategy = await _strategyRepo.GetByIdAsync(order.StrategyId.Value, ct).ConfigureAwait(false);
                if (strategy is not null)
                {
                    leverage = strategy.Configuration.Leverage;
                    strategyType = strategy.StrategyType;
                }
            }
            catch (Exception sEx)
            {
                _logger.LogWarning(sEx,
                    "[ORDER_FILLED_HANDLER] Strategy resolve failed for {StrategyId} (Order {Cid}); using defaults.",
                    order.StrategyId.Value, order.ClientOrderId);
            }
        }

        try
        {
            var newPosition = Position.Open(
                symbol: order.Symbol,
                side: openSide.Value,
                quantity: order.FilledQuantity,
                entryPrice: order.AverageFillPrice,
                leverage: leverage,
                marginMode: MarginMode.Isolated,
                stopLossPrice: null,
                takeProfitPrice: null,
                strategyId: order.StrategyId,
                strategyType: strategyType,
                parametersSnapshot: null);  // S77: snapshot 由 StrategyExecutor materialization 帶, handler 補建留 null
            newPosition.AddCommission(order.Commission);

            await _positionRepo.AddAsync(newPosition, ct).ConfigureAwait(false);
            await _uow.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[ORDER_FILLED_HANDLER] Auto-materialized Position from Order {Cid}: {Symbol} {Side} qty={Qty} entry={Entry}.",
                order.ClientOrderId, order.Symbol, openSide.Value, order.FilledQuantity.Value,
                order.AverageFillPrice.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[ORDER_FILLED_HANDLER] Position materialization failed for Order {Cid} — AccountSynchronizer Bug 11 reconcile is fallback.",
                order.ClientOrderId);
        }
    }
}
