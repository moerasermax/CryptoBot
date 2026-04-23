using System.Text.Json;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
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

    /// <summary>
    /// S42 T1：上一次完成 <see cref="IStrategy.AnalyzeAsync"/> 的 UTC 時間。
    /// 未執行過則為 <c>null</c>。純 in-process 心跳指標，不落 DB。
    /// </summary>
    public DateTime? LastEvaluatedAtUtc { get; private set; }

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

            // S42 T1 / S44 T1：每次評估完就廣播心跳 — 不管 Signal 是不是 None。
            // Dashboard 靠這個事件證明「策略大腦還在跑」而不是卡在某處；
            // S44 擴充：payload 帶上 StrategyName / SignalType / Note（= TradingSignal.Reason），
            // Dashboard 決策日誌面板靠這三個欄位上色（INF 灰 / SIGNAL 綠紅）與顯示原因。
            // broadcast 失敗不阻擋主流程（策略繼續跑比 UI 亮燈更重要）。
            LastEvaluatedAtUtc = DateTime.UtcNow;
            try
            {
                await _broadcaster.BroadcastStrategyEvaluatedAsync(new StrategyEvaluatedUpdate(
                    StrategyId: _strategy.Id,
                    StrategyName: _strategy.Name,
                    EvaluatedAtUtc: LastEvaluatedAtUtc.Value,
                    Symbol: cfg.Symbol.BingXFormat,
                    Interval: cfg.Interval.ToString(),
                    LastClosePrice: kline.Close,
                    SignalType: signal.Type.ToString(),
                    Note: signal.Reason,
                    // S45：Dashboard 靠這欄位即時同步卡片的模型標籤，不必重拉 /api/strategies
                    StrategyType: _strategy.StrategyType), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat broadcast failed for {Name}.", _strategy.Name);
            }

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

            // S44 T1：把錯誤推給 Dashboard 決策日誌（橘色 [ERROR]），
            // 讓使用者能看到每次失敗的原因，不用翻 log 檔。
            try
            {
                await _broadcaster.BroadcastStrategyEvaluationFailedAsync(new StrategyEvaluationFailedUpdate(
                    StrategyId: _strategy.Id,
                    StrategyName: _strategy.Name,
                    OccurredAtUtc: DateTime.UtcNow,
                    Symbol: _strategy.Configuration.Symbol.BingXFormat,
                    ErrorMessage: ex.Message), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception broadcastEx)
            {
                _logger.LogWarning(broadcastEx,
                    "Failed to broadcast evaluation-failed event for {Name}.", _strategy.Name);
            }

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
        var positionRepo = sp.GetRequiredService<IPositionRepository>();
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
        await orderRepo.AddAsync(order, CancellationToken.None).ConfigureAwait(false);
        _cooldownTracker.RecordOrderPlaced(_strategy.Id);

        // 5) S99-S43 T4：開單後必須「立刻建立本地 Position」— 否則 Dashboard 的
        //    GetOpenPositionsAsync 會拿不到任何資料，使用者看到「持倉隱形」的錯覺。
        //    作法參照舊 Trading/StrategyExecutor：短暫等待 → 刷新成交狀態 → 若 Filled
        //    則 Position.Open + AddAsync。只對 Open* 訊號做；Close* 交給 AccountSynchronizer
        //    的 WS HandleAccountUpdate 處理（那邊會偵測 remote.Quantity == 0 自動 Close()）。
        Position? newPosition = null;
        var isOpenSignal = signal.Type == SignalType.OpenLong || signal.Type == SignalType.OpenShort;
        if (isOpenSignal && order.IsActive)
        {
            try
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                await _exchange.RefreshOrderStatusAsync(order, CancellationToken.None).ConfigureAwait(false);
                await orderRepo.UpdateAsync(order, CancellationToken.None).ConfigureAwait(false);

                if (order.Status == OrderStatus.Filled && order.AverageFillPrice is not null)
                {
                    var parametersSnapshot = BuildParametersSnapshot(_strategy.Configuration);
                    newPosition = Position.Open(
                        symbol: signal.Symbol,
                        side: positionSide,
                        quantity: order.FilledQuantity,
                        entryPrice: order.AverageFillPrice,
                        leverage: _strategy.Configuration.Leverage,
                        marginMode: MarginMode.Isolated,
                        stopLossPrice: signal.SuggestedStopLoss,
                        takeProfitPrice: signal.SuggestedTakeProfit,
                        strategyId: _strategy.Id,
                        strategyType: _strategy.StrategyType,
                        parametersSnapshot: parametersSnapshot);
                    newPosition.AddCommission(order.Commission);
                    if (_strategy.Configuration.TrailingStopPercent is decimal trailPct)
                        newPosition.EnableTrailingStop(trailPct);
                    await positionRepo.AddAsync(newPosition, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-order position materialization failed for {Symbol} — AccountSynchronizer will retry via WS.",
                    signal.Symbol);
            }
        }

        // 6) S99-S43 T4：SaveChanges 必須在 BroadcastTradeAsync 之前完成，
        //    否則 UI 收到 SignalR 事件去打 /api/dashboard/stats 時，DB 還看不到 Order/Position，
        //    Active Positions 會呈現「空了一瞬間」的隱形狀態。
        // S53 T2：走重試版 — AccountSynchronizer WS 事件可能已經先一步 UPDATE 了 order row，
        //    SaveChangesAsync 會噴 DbUpdateConcurrencyException；重試會 reload DB 值再套 client 改動，
        //    確保訂單 / 倉位在任何時序下都能落地。
        await uow.SaveChangesWithRetryAsync(ct: CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Order placed: {Side} {PositionSide} {Qty} {Symbol} (exchangeId={ExId}, positionCreated={Pos})",
            orderSide, positionSide, qty, signal.Symbol,
            order.ExchangeOrderId ?? "pending",
            newPosition is not null);

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

        // Web UI 即時推播 — **DB 已 commit 後**才廣播，UI 重拉資料必能看到新倉位。
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

    /// <summary>
    /// S39 / S99-S43 T4：序列化 StrategyConfiguration 的決策相關欄位 — Position 用來做歷史複盤，
    /// 即便未來 Strategy 被改名或 Parameters 被熱更新，歷史 row 仍能還原當時決策現場。
    /// </summary>
    private static string BuildParametersSnapshot(StrategyConfiguration config)
    {
        var payload = new
        {
            leverage = config.Leverage.Value,
            riskPerTradePercent = config.RiskPerTradePercent,
            stopLossPercent = config.StopLossPercent,
            takeProfitPercent = config.TakeProfitPercent,
            trailingStopPercent = config.TrailingStopPercent,
            parameters = config.Parameters,
        };
        return JsonSerializer.Serialize(payload);
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
