using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.Services;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Trading;

/// <summary>
/// 策略執行器 - 連接策略、風控與交易所的核心協調者
/// 
/// 主要工作流程：
/// 1. 接收市場快照 (K 線 + 當前價)
/// 2. 呼叫策略產生訊號
/// 3. 若為開倉訊號 → 計算倉位大小 → 風控檢查 → 下單 → 建立 Position
/// 4. 若為平倉訊號 → 下平倉單 → 更新 Position
/// 5. 持倉價格更新 → 檢查止損止盈
/// </summary>
public sealed class StrategyExecutor
{
    private readonly IStrategy _strategy;
    private readonly IExchangeClient _exchange;
    private readonly IRiskManager _riskManager;
    private readonly IOrderRepository _orderRepository;
    private readonly IPositionRepository _positionRepository;
    private readonly IStrategyRepository _strategyRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationService _notifications;
    private readonly ILogger<StrategyExecutor> _logger;

    public StrategyExecutor(
        IStrategy strategy,
        IExchangeClient exchange,
        IRiskManager riskManager,
        IOrderRepository orderRepository,
        IPositionRepository positionRepository,
        IStrategyRepository strategyRepository,
        IUnitOfWork unitOfWork,
        INotificationService notifications,
        ILogger<StrategyExecutor> logger)
    {
        _strategy = strategy;
        _exchange = exchange;
        _riskManager = riskManager;
        _orderRepository = orderRepository;
        _positionRepository = positionRepository;
        _strategyRepository = strategyRepository;
        _unitOfWork = unitOfWork;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// 單輪策略執行 (由排程或 WebSocket 事件驅動)
    /// </summary>
    public async Task ExecuteTickAsync(
        Strategy strategyState,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        CancellationToken ct = default)
    {
        if (strategyState.Status != StrategyStatus.Running)
            return;

        try
        {
            // 1. 取得該策略目前持倉
            var openPositions = await _positionRepository.GetByStrategyIdAsync(
                strategyState.Id, includeClosedPositions: false, ct);

            // 2. 先檢查是否有止損止盈需要觸發
            foreach (var position in openPositions)
            {
                var triggerSignal = position.UpdateCurrentPrice(snapshot.FuturesMarkPrice);
                if (triggerSignal != SignalType.None)
                {
                    await _positionRepository.UpdateAsync(position, ct);
                    await ClosePositionAsync(position, snapshot.FuturesMarkPrice,
                        $"SL/TP triggered: {triggerSignal}", ct);
                }
                else
                {
                    await _positionRepository.UpdateAsync(position, ct);
                }
            }

            // 重新取得持倉 (可能有剛剛被平掉的)
            openPositions = await _positionRepository.GetByStrategyIdAsync(
                strategyState.Id, includeClosedPositions: false, ct);

            // 3. 呼叫策略分析
            var signal = await _strategy.AnalyzeAsync(
                strategyState.Configuration, klines, snapshot, openPositions, ct);

            if (signal.Type == SignalType.None)
                return;

            _logger.LogInformation("Strategy {StrategyName} generated signal: {Signal}",
                strategyState.Name, signal);

            // 4. 根據訊號類型執行
            switch (signal.Type)
            {
                case SignalType.OpenLong:
                case SignalType.OpenShort:
                    await HandleOpenSignalAsync(strategyState, signal, ct);
                    break;

                case SignalType.CloseLong:
                case SignalType.CloseShort:
                    var posToClose = openPositions.FirstOrDefault(p =>
                        (signal.Type == SignalType.CloseLong && p.Side == PositionSide.Long)
                        || (signal.Type == SignalType.CloseShort && p.Side == PositionSide.Short));
                    if (posToClose is not null)
                        await ClosePositionAsync(
                            posToClose, signal.SuggestedPrice, signal.Reason, ct);
                    break;
            }

            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strategy {StrategyName} execution failed", strategyState.Name);
            strategyState.ReportError(ex.Message);
            await _strategyRepository.UpdateAsync(strategyState, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            await _notifications.NotifyErrorAsync(ex, ct);
        }
    }

    private async Task HandleOpenSignalAsync(
        Strategy strategyState,
        TradingSignal signal,
        CancellationToken ct)
    {
        // 1. 取得交易規則
        var rules = await _exchange.GetTradingRulesAsync(signal.Symbol, ct);
        var balance = await _exchange.GetFuturesBalanceAsync(ct: ct);

        // 2. 計算倉位大小
        if (signal.SuggestedStopLoss is null)
        {
            _logger.LogWarning("Signal without stop loss rejected for safety");
            return;
        }

        Quantity quantity;
        try
        {
            quantity = PositionSizingService.CalculatePositionSize(
                accountBalance: balance,
                riskPercent: strategyState.Configuration.RiskPerTradePercent,
                entryPrice: signal.SuggestedPrice,
                stopLossPrice: signal.SuggestedStopLoss,
                leverage: strategyState.Configuration.Leverage,
                minQuantity: rules.MinQuantity,
                stepSize: rules.StepSize);
            quantity = PositionSizingService.AdjustByConfidence(quantity, signal.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cannot calculate position size: {Reason}", ex.Message);
            return;
        }

        // 3. 風控檢查
        var riskCheck = await _riskManager.CheckBeforeOpenAsync(
            strategyState, signal, quantity, ct);
        if (!riskCheck.IsApproved)
        {
            _logger.LogWarning("Risk check rejected: {Reason}", riskCheck.Reason);
            await _notifications.NotifyAsync(
                "Trade rejected", riskCheck.Reason!, NotificationLevel.Warning, ct);
            return;
        }

        // 4. 確保槓桿設定
        await _exchange.SetLeverageAsync(
            signal.Symbol, strategyState.Configuration.Leverage, ct);

        // 5. 建立並送出市價單
        var side = signal.Type == SignalType.OpenLong ? OrderSide.Buy : OrderSide.Sell;
        var posSide = signal.Type == SignalType.OpenLong
            ? PositionSide.Long : PositionSide.Short;

        var order = Order.CreateMarketOrder(
            signal.Symbol, side, posSide, quantity, strategyState.Id);
        await _orderRepository.AddAsync(order, ct);

        try
        {
            await _exchange.PlaceOrderAsync(order, ct);
            await _orderRepository.UpdateAsync(order, ct);
        }
        catch (Exception ex)
        {
            order.Reject(ex.Message);
            await _orderRepository.UpdateAsync(order, ct);
            _logger.LogError(ex, "Failed to place order");
            return;
        }

        // 6. 等待成交確認並建立 Position
        // 注意: 真實環境下應透過 WebSocket 的 OrderUpdate 事件處理,
        //       此處簡化為輪詢 + 樂觀建立 Position
        await Task.Delay(500, ct);
        await _exchange.RefreshOrderStatusAsync(order, ct);
        await _orderRepository.UpdateAsync(order, ct);

        if (order.Status == OrderStatus.Filled && order.AverageFillPrice is not null)
        {
            var position = Position.Open(
                symbol: signal.Symbol,
                side: posSide,
                quantity: order.FilledQuantity,
                entryPrice: order.AverageFillPrice,
                leverage: strategyState.Configuration.Leverage,
                marginMode: MarginMode.Isolated,
                stopLossPrice: signal.SuggestedStopLoss,
                takeProfitPrice: signal.SuggestedTakeProfit,
                strategyId: strategyState.Id);

            position.AddCommission(order.Commission);

            if (strategyState.Configuration.TrailingStopPercent is decimal trailPct)
                position.EnableTrailingStop(trailPct);

            await _positionRepository.AddAsync(position, ct);

            await _notifications.NotifyTradeAsync(
                signal.Symbol.ToString(),
                $"Opened {posSide}",
                order.AverageFillPrice.Value,
                order.FilledQuantity.Value,
                ct);
        }
    }

    private async Task ClosePositionAsync(
        Position position, Price closePrice, string reason, CancellationToken ct)
    {
        var closeSide = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

        var closeOrder = Order.CreateMarketOrder(
            position.Symbol, closeSide, position.Side, position.Quantity, position.StrategyId);
        await _orderRepository.AddAsync(closeOrder, ct);

        try
        {
            await _exchange.PlaceOrderAsync(closeOrder, ct);
            await _orderRepository.UpdateAsync(closeOrder, ct);
            await Task.Delay(500, ct);
            await _exchange.RefreshOrderStatusAsync(closeOrder, ct);
            await _orderRepository.UpdateAsync(closeOrder, ct);

            if (closeOrder.Status == OrderStatus.Filled && closeOrder.AverageFillPrice is not null)
            {
                position.Close(closeOrder.AverageFillPrice, reason, closeOrder.Commission);
                await _positionRepository.UpdateAsync(position, ct);

                // 更新策略績效
                if (position.StrategyId.HasValue)
                {
                    var strategy = await _strategyRepository.GetByIdAsync(
                        position.StrategyId.Value, ct);
                    if (strategy is not null)
                    {
                        strategy.RecordTradeResult(position.RealizedPnL);
                        await _strategyRepository.UpdateAsync(strategy, ct);
                    }
                }

                await _notifications.NotifyTradeAsync(
                    position.Symbol.ToString(),
                    $"Closed {position.Side} (PnL: {position.RealizedPnL:F4})",
                    closeOrder.AverageFillPrice.Value,
                    position.Quantity.Value,
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close position {PositionId}", position.Id);
            await _notifications.NotifyErrorAsync(ex, ct);
        }
    }
}
