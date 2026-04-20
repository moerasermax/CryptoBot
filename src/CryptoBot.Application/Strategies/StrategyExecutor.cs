using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Strategies;

/// <summary>
/// <see cref="IStrategyExecutor"/> 的實作。
///
/// 管線（每根收盤 K 線觸發一次）：
/// <code>
/// IMarketDataStream.OnKlineUpdate
///   ↓ (過濾 Symbol + Interval)
/// HandleKlineUpdateAsync
///   ↓ (SemaphoreSlim 確保同一 Executor 不重入)
/// 1. 更新滾動 K 線視窗
/// 2. GetMarketSnapshotAsync
/// 3. 取得該策略未關閉的 Position
/// 4. IStrategy.AnalyzeAsync → TradingSignal
/// 5. 若 Signal != None：Sizer → RiskManager → PlaceOrderAsync → Repo.AddAsync
/// </code>
///
/// 錯誤策略：單次 Analyze/下單例外 log 並增加連續錯誤計數，累計達
/// <see cref="ConsecutiveErrorThreshold"/> 會自動停機（避免壞掉的策略持續吃 API 額度）。
/// </summary>
public sealed class StrategyExecutor : IStrategyExecutor
{
    private const int ConsecutiveErrorThreshold = 5;

    private readonly Strategy _strategy;
    private readonly IStrategy _strategyImpl;
    private readonly IMarketDataStream _marketData;
    private readonly IExchangeClient _exchange;
    private readonly IStrategyCooldownTracker _cooldownTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotificationService _notifications;
    private readonly IRealtimeBroadcaster _broadcaster;
    private readonly ILogger<StrategyExecutor> _logger;

    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly LinkedList<Kline> _klineBuffer = new();
    private readonly object _bufferLock = new();

    private Func<Symbol, KlineInterval, Kline, Task>? _klineHandler;
    private int _consecutiveErrors;
    private bool _running;
    private bool _disposed;

    public StrategyExecutor(
        Strategy strategy,
        IStrategy strategyImpl,
        IMarketDataStream marketData,
        IExchangeClient exchange,
        IStrategyCooldownTracker cooldownTracker,
        IServiceScopeFactory scopeFactory,
        INotificationService notifications,
        IRealtimeBroadcaster broadcaster,
        ILogger<StrategyExecutor> logger)
    {
        _strategy = strategy;
        _strategyImpl = strategyImpl;
        _marketData = marketData;
        _exchange = exchange;
        _cooldownTracker = cooldownTracker;
        _scopeFactory = scopeFactory;
        _notifications = notifications;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public Guid StrategyId => _strategy.Id;
    public bool IsRunning => _running;

    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_running) return;

        var cfg = _strategy.Configuration;
        _logger.LogInformation(
            "Starting strategy {Name} ({Type}) on {Symbol}@{Interval}",
            _strategy.Name, _strategy.StrategyType, cfg.Symbol, cfg.Interval);

        // 1) 預載歷史 K 線塞滿視窗（舊→新）
        var history = await _exchange.GetKlinesAsync(
            cfg.Symbol, cfg.Interval, limit: cfg.MaxKlineWindow, ct: ct).ConfigureAwait(false);
        lock (_bufferLock)
        {
            _klineBuffer.Clear();
            foreach (var k in history) _klineBuffer.AddLast(k);
            TrimBufferLocked(cfg.MaxKlineWindow);
        }

        // 2) 訂閱事件
        _klineHandler = HandleKlineUpdateAsync;
        _marketData.OnKlineUpdate += _klineHandler;

        // 3) 確保資料流已啟動並訂閱對應 Symbol+Interval
        await _marketData.StartAsync(ct).ConfigureAwait(false);
        await _marketData.SubscribeKlinesAsync(cfg.Symbol, cfg.Interval, ct).ConfigureAwait(false);

        _running = true;
        _logger.LogInformation(
            "Strategy {Name} started with {Count} historical klines preloaded.",
            _strategy.Name, history.Count);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_running) return;
        _running = false;

        _logger.LogInformation("Stopping strategy {Name}…", _strategy.Name);

        if (_klineHandler is not null)
        {
            _marketData.OnKlineUpdate -= _klineHandler;
            _klineHandler = null;
        }

        try
        {
            await _marketData.UnsubscribeAsync(_strategy.Configuration.Symbol, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unsubscribe failed for {Symbol} — continuing shutdown.",
                _strategy.Configuration.Symbol);
        }

        // 等待進行中的 tick 完成
        await _processLock.WaitAsync(ct).ConfigureAwait(false);
        _processLock.Release();

        _logger.LogInformation("Strategy {Name} stopped.", _strategy.Name);
    }

    private async Task HandleKlineUpdateAsync(Symbol symbol, KlineInterval interval, Kline kline)
    {
        if (_disposed || !_running) return;

        // 只處理屬於本策略的 K 線
        if (!symbol.Equals(_strategy.Configuration.Symbol)) return;
        if (interval != _strategy.Configuration.Interval) return;

        // 非 blocking — 若上一根還沒處理完，就跳過這根（策略不該搶同一視窗）
        if (!await _processLock.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Strategy {Name} still processing previous kline — skipping {OpenTime}.",
                _strategy.Name, kline.OpenTime);
            return;
        }

        try
        {
            await ProcessKlineAsync(kline).ConfigureAwait(false);
        }
        finally
        {
            _processLock.Release();
        }
    }

    private async Task ProcessKlineAsync(Kline kline)
    {
        try
        {
            // 1) 更新滾動視窗
            var cfg = _strategy.Configuration;
            IReadOnlyList<Kline> klinesSnapshot;
            lock (_bufferLock)
            {
                _klineBuffer.AddLast(kline);
                TrimBufferLocked(cfg.MaxKlineWindow);
                klinesSnapshot = _klineBuffer.ToArray();  // 交付給策略的快照
            }

            // 2) 市場快照
            var snapshot = await _exchange
                .GetMarketSnapshotAsync(cfg.Symbol, CancellationToken.None)
                .ConfigureAwait(false);

            // 3) 開 DI scope 取 scoped 服務（repositories, RiskManager, Sizer, UnitOfWork）
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;

            var positionRepo = sp.GetRequiredService<IPositionRepository>();
            var openPositions = await positionRepo
                .GetByStrategyIdAsync(_strategy.Id, includeClosedPositions: false, CancellationToken.None)
                .ConfigureAwait(false);

            // 4) 執行策略
            var signal = await _strategyImpl
                .AnalyzeAsync(cfg, klinesSnapshot, snapshot, openPositions, CancellationToken.None)
                .ConfigureAwait(false);

            if (signal.Type == SignalType.None)
            {
                _consecutiveErrors = 0;
                return;
            }

            _logger.LogInformation(
                "Strategy {Name} signal: {Signal}",
                _strategy.Name, signal);

            await HandleSignalAsync(sp, signal).ConfigureAwait(false);
            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            _logger.LogError(ex,
                "Strategy {Name} errored on kline {OpenTime} (consecutive={Count}).",
                _strategy.Name, kline.OpenTime, _consecutiveErrors);

            if (_consecutiveErrors >= ConsecutiveErrorThreshold)
            {
                _logger.LogError(
                    "Strategy {Name} hit {Threshold} consecutive errors — stopping.",
                    _strategy.Name, ConsecutiveErrorThreshold);
                await SelfStopOnErrorAsync(ex.Message).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleSignalAsync(IServiceProvider sp, TradingSignal signal)
    {
        var sizer = sp.GetRequiredService<IOrderSizer>();
        var risk = sp.GetRequiredService<IRiskManager>();
        var orderRepo = sp.GetRequiredService<IOrderRepository>();
        var uow = sp.GetRequiredService<IUnitOfWork>();

        // 1) 計算目標數量
        var qty = await sizer.ComputeAsync(_strategy, signal, CancellationToken.None).ConfigureAwait(false);
        if (qty.Value <= 0)
        {
            _logger.LogWarning("Sizer returned zero quantity for {Signal} — skipping.", signal);
            return;
        }

        // 2) 風控
        var check = await risk.CheckBeforeOpenAsync(_strategy, signal, qty, CancellationToken.None)
            .ConfigureAwait(false);
        if (!check.IsApproved)
        {
            _logger.LogWarning("Order rejected by RiskManager: {Reason}", check.Reason);
            return;
        }

        // 3) 建 Order aggregate
        var (orderSide, positionSide) = MapSignalToOrderSides(signal.Type);
        var order = Order.CreateMarketOrder(
            symbol: signal.Symbol,
            side: orderSide,
            positionSide: positionSide,
            quantity: qty,
            strategyId: _strategy.Id);

        // 4) 下單
        await _exchange.PlaceOrderAsync(order, CancellationToken.None).ConfigureAwait(false);

        // 5) 寫入 repo + 記錄冷卻
        await orderRepo.AddAsync(order, CancellationToken.None).ConfigureAwait(false);
        await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        _cooldownTracker.RecordOrderPlaced(_strategy.Id);

        _logger.LogInformation(
            "Order placed: {Side} {PositionSide} {Qty} {Symbol} (exchangeId={ExId})",
            orderSide, positionSide, qty, signal.Symbol, order.ExchangeOrderId ?? "pending");

        // S7 全線試車的醒目標記 — 確認管線全線串通
        _logger.LogInformation(
            "🚀 [STRATEGY-MATCH] {Symbol} {PositionSide} signal triggered! Order placed.",
            signal.Symbol, positionSide);

        // 外部通知（未配置時注入的是 NoOp，不會拋例外；配置後走 Discord webhook）
        try
        {
            await _notifications.NotifyTradeAsync(
                symbol: signal.Symbol.BingXFormat,
                action: $"{orderSide} {positionSide} ({_strategy.Name})",
                price: order.AverageFillPrice?.Value ?? 0m,
                quantity: qty.Value,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification dispatch failed for STRATEGY-MATCH on {Symbol}.", signal.Symbol);
        }

        // Web UI 即時推播（無 Web host 時注入 NullRealtimeBroadcaster，不會做事）
        try
        {
            await _broadcaster.BroadcastTradeAsync(new TradeFilledUpdate(
                Timestamp: DateTime.UtcNow,
                Symbol: signal.Symbol.BingXFormat,
                Side: orderSide.ToString(),
                PositionSide: positionSide.ToString(),
                Quantity: qty.Value,
                Price: order.AverageFillPrice?.Value ?? signal.SuggestedPrice.Value,
                StrategyName: _strategy.Name), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime broadcast failed for STRATEGY-MATCH on {Symbol}.", signal.Symbol);
        }
    }

    private async Task SelfStopOnErrorAsync(string reason)
    {
        try
        {
            _strategy.ReportError(reason);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var strategyRepo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await strategyRepo.UpdateAsync(_strategy, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist error state for {Name}.", _strategy.Name);
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during self-stop of {Name}.", _strategy.Name);
        }
    }

    private static (OrderSide orderSide, PositionSide positionSide) MapSignalToOrderSides(SignalType type) =>
        type switch
        {
            SignalType.OpenLong   => (OrderSide.Buy,  PositionSide.Long),
            SignalType.OpenShort  => (OrderSide.Sell, PositionSide.Short),
            SignalType.CloseLong  => (OrderSide.Sell, PositionSide.Long),
            SignalType.CloseShort => (OrderSide.Buy,  PositionSide.Short),
            _ => throw new InvalidOperationException($"Unexpected signal type {type} at order mapping stage."),
        };

    private void TrimBufferLocked(int max)
    {
        while (_klineBuffer.Count > max)
            _klineBuffer.RemoveFirst();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StrategyExecutor));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* swallow during dispose */ }
        _processLock.Dispose();
    }
}
