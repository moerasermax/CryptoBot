using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Strategies;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 時光機 — 把歷史 K 線由舊到新一根一根餵給 <see cref="IStrategy"/>，
/// 把策略吐出的訊號交給 <see cref="IExchangeClient"/>（實務上由 BacktestSimulator 承接）成交。
///
/// 相較 live 版 <see cref="StrategyExecutor"/>：
/// - 不落 DbContext / Repository / UnitOfWork — 結果累積在記憶體 <see cref="BacktestReport"/> 中。
/// - 不走 RiskManager / Sizer — 用 <see cref="BacktestOptions"/> 的比例簡化下單量。
/// - <b>持倉狀態完全由 Engine 託管</b>：OpenLong/OpenShort 就新建 Position 並登錄；
///   CloseLong/CloseShort 時找對應倉位 Close()，把 RealizedPnL 回灌虛擬餘額，
///   並把 openPositions 清單轉給 <see cref="IStrategy.AnalyzeAsync"/> — 如此策略才有「我現在有多單 / 空單」這個判斷依據。
///
/// 不變式：策略看到的 <c>klines</c> 永遠只包含「截至目前這根（含）」為止的資料，嚴禁 look-ahead。
/// </summary>
public sealed class BacktestEngine
{
    /// <summary>
    /// S25 T2：權益曲線上限點數。長週期回測（例 1 年 1m = 525k 根）若每根都記會吃掉 ~8 MB；
    /// ×100 參數組合 = 800 MB 不可接受。用 stride 下採樣到固定 ≤1000 點。
    /// </summary>
    public const int MaxEquityPoints = 1000;

    private readonly IHistoricalKlineStore _store;
    private readonly IBacktestClock _clock;
    private readonly IExchangeClient _exchange;
    private readonly IStrategy _strategy;
    private readonly ILogger<BacktestEngine> _logger;

    public BacktestEngine(
        IHistoricalKlineStore store,
        IBacktestClock clock,
        IExchangeClient exchange,
        IStrategy strategy,
        ILogger<BacktestEngine> logger)
    {
        _store = store;
        _clock = clock;
        _exchange = exchange;
        _strategy = strategy;
        _logger = logger;
    }

    public async Task<BacktestReport> RunAsync(
        BacktestOptions options,
        StrategyConfiguration strategyConfig,
        Guid strategyId,
        CancellationToken ct = default)
    {
        var symbol = Symbol.Parse(options.Symbol);
        var windowSize = Math.Max(options.WarmupBars, strategyConfig.MaxKlineWindow);

        _logger.LogInformation(
            "⏳ [BACKTEST] Replay {Symbol} {Interval} [{Start:yyyy-MM-dd} .. {End:yyyy-MM-dd}] warmup={Warmup}",
            options.Symbol, options.Interval, options.StartTime, options.EndTime, windowSize);

        var startingBalance = await _exchange.GetFuturesBalanceAsync("USDT", ct).ConfigureAwait(false);

        var window = new LinkedList<Kline>();
        var fills = new List<Order>();
        var openPositions = new List<Position>();
        var closedPositions = new List<Position>();
        var totalKlines = 0;
        var signalsTriggered = 0;
        DateTime? firstTime = null;
        DateTime? lastTime = null;

        // 權益高水位 + 最大回撤追蹤。每根 K 線收盤重算一次 mark-to-market 權益，
        // peak 永遠只往上，drawdown = (peak - now) / peak。
        var peakEquity = startingBalance;
        var maxDrawdownPct = 0m;
        var isLiquidated = false;

        // S25 T2：權益曲線下採樣。stride 從預期 K 線總數估出，保證序列 ≤ MaxEquityPoints 點。
        // 估算失敗（interval=0 或時間區間反了）時退化為 stride=1，反正真實點數自然上限是 totalKlines。
        var intervalSpan = options.Interval.ToTimeSpan();
        var expectedKlines = intervalSpan > TimeSpan.Zero && options.EndTime > options.StartTime
            ? (int)((options.EndTime - options.StartTime).Ticks / intervalSpan.Ticks)
            : 0;
        var stride = Math.Max(1, expectedKlines / MaxEquityPoints);
        var equityCurve = new List<EquityPoint>(Math.Min(expectedKlines, MaxEquityPoints) + 1);

        await foreach (var kline in _store
            .StreamRangeAsync(symbol, options.Interval, options.StartTime, options.EndTime, ct)
            .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            totalKlines++;
            firstTime ??= kline.OpenTime;
            lastTime = kline.OpenTime;

            // 先進視窗，再推進時鐘 — 模擬器此刻看到的就是策略此刻看到的
            window.AddLast(kline);
            while (window.Count > windowSize) window.RemoveFirst();
            _clock.AdvanceTo(kline);

            // 讓每根 K 線都先把當前價餵給 open positions — 觸發止損/止盈時自動產生 Close 訊號
            TickOpenPositions(kline, openPositions, closedPositions, strategyId, options);

            // 預熱期間先不評估策略，只累積視窗
            if (window.Count < windowSize) continue;

            var order = await EvaluateAndMaybePlaceAsync(
                symbol, strategyConfig, strategyId, window, options,
                openPositions, closedPositions, ct).ConfigureAwait(false);

            if (order is not null)
            {
                signalsTriggered++;
                fills.Add(order);
            }

            // Mark-to-market 權益：已實現的 VirtualBalance + 未平倉 Position 的 UnrealizedPnL
            var openUnrealized = openPositions.Sum(p => p.UnrealizedPnL);
            var currentEquity = await _exchange.GetFuturesBalanceAsync("USDT", ct).ConfigureAwait(false)
                              + openUnrealized;

            // S32-T1：爆倉檢查放在權益計算「之後、回撤記錄之前」。觸發後 Simulator 會把
            // VirtualBalance 強制歸零；此處將 currentEquity 同步歸零，後續的 peak/drawdown
            // 與權益曲線才會一致反映「帳戶已爆」。Break 立刻中止主迴圈，不再處理後續 K 線。
            if (_clock.CheckAndApplyLiquidation(openUnrealized))
            {
                isLiquidated = true;
                currentEquity = 0m;
                if (peakEquity > 0)
                {
                    var ddL = (peakEquity - currentEquity) / peakEquity * 100m;
                    if (ddL > maxDrawdownPct) maxDrawdownPct = ddL;
                }
                equityCurve.Add(new EquityPoint(kline.OpenTime, 0m));
                _logger.LogWarning(
                    "💥 [BACKTEST] Liquidated at {Time:yyyy-MM-dd HH:mm}. Halting replay — {N} klines processed.",
                    kline.OpenTime, totalKlines);
                break;
            }

            if (currentEquity > peakEquity) peakEquity = currentEquity;
            if (peakEquity > 0)
            {
                var dd = (peakEquity - currentEquity) / peakEquity * 100m;
                if (dd > maxDrawdownPct) maxDrawdownPct = dd;
            }

            // S25 T2：每 stride 根記一次權益點。totalKlines 從 1 起算，用 (totalKlines - 1) 對齊起點。
            if (((totalKlines - 1) % stride) == 0)
                equityCurve.Add(new EquityPoint(kline.OpenTime, currentEquity));
        }

        // 末尾補一筆：若最後一根 K 線不在 stride 節奏上，UI 上的曲線尾部會缺一段 — 這裡補齊。
        if (lastTime is not null
            && (equityCurve.Count == 0 || equityCurve[^1].TimeUtc != lastTime.Value))
        {
            var finalEquity = await _exchange.GetFuturesBalanceAsync("USDT", ct).ConfigureAwait(false)
                            + openPositions.Sum(p => p.UnrealizedPnL);
            equityCurve.Add(new EquityPoint(lastTime.Value, finalEquity));
        }

        var endingBalance = await _exchange.GetFuturesBalanceAsync("USDT", ct).ConfigureAwait(false);

        var report = new BacktestReport(
            TotalKlines: totalKlines,
            SignalsTriggered: signalsTriggered,
            OrdersFilled: fills.Count,
            StartingBalance: startingBalance,
            EndingBalance: endingBalance,
            PeakEquity: peakEquity,
            MaxDrawdownPercent: maxDrawdownPct,
            FirstKlineTime: firstTime,
            LastKlineTime: lastTime,
            Fills: fills,
            EquityCurve: equityCurve,
            IsLiquidated: isLiquidated);

        _logger.LogInformation(
            "✅ [BACKTEST] Done. klines={N} signals={S} fills={F} openLeft={Open} closed={Closed} bal {From:F2} → {To:F2} ({Pct:F2}%) maxDD={DD:F2}%",
            totalKlines, signalsTriggered, fills.Count, openPositions.Count, closedPositions.Count,
            startingBalance, endingBalance, report.ReturnPercent, maxDrawdownPct);

        return report;
    }

    private async Task<Order?> EvaluateAndMaybePlaceAsync(
        Symbol symbol,
        StrategyConfiguration strategyConfig,
        Guid strategyId,
        LinkedList<Kline> window,
        BacktestOptions options,
        List<Position> openPositions,
        List<Position> closedPositions,
        CancellationToken ct)
    {
        var klinesSnapshot = window.ToArray();
        var snapshot = await _exchange.GetMarketSnapshotAsync(symbol, ct).ConfigureAwait(false);

        // 持倉傳入：策略才能正確選擇「開新倉」vs「平現倉」。
        var signal = await _strategy
            .AnalyzeAsync(strategyConfig, klinesSnapshot, snapshot, openPositions, ct)
            .ConfigureAwait(false);

        if (signal.Type == SignalType.None) return null;

        var currentClose = window.Last!.Value.Close;
        return await ExecuteSignalAsync(
            signal, currentClose, strategyConfig, strategyId, options,
            openPositions, closedPositions, ct).ConfigureAwait(false);
    }

    private async Task<Order?> ExecuteSignalAsync(
        TradingSignal signal,
        decimal currentClose,
        StrategyConfiguration strategyConfig,
        Guid strategyId,
        BacktestOptions options,
        List<Position> openPositions,
        List<Position> closedPositions,
        CancellationToken ct)
    {
        var (side, positionSide) = MapSignalToOrderSides(signal.Type);

        // 平倉訊號需綁定現有倉位的數量，否則 Order 數量會對不上 Position。
        Quantity qty;
        Position? closingPosition = null;
        if (IsCloseSignal(signal.Type))
        {
            closingPosition = openPositions.FirstOrDefault(p => p.Side == positionSide && !p.IsClosed);
            if (closingPosition is null)
            {
                _logger.LogWarning(
                    "Signal {Type} received but no open {Side} position to close — skipping.",
                    signal.Type, positionSide);
                return null;
            }
            qty = closingPosition.Quantity;
        }
        else
        {
            // S32-T1：用戶自訂槓桿放大開倉名目金額。10% 自有資金 × 槓桿倍數 = 本次名目部位。
            // 1x 時行為與 S32 前相同（向下相容 VCP-Accuracy）；高槓桿（例如 100x）會讓 UnrealizedPnL 也
            // 等比例放大，進而在 BacktestSimulator.CheckAndApplyLiquidation 中快速觸發爆倉。
            var leverage = (decimal)strategyConfig.Leverage.Value;
            var notional = Math.Max(options.InitialBalance * 0.1m * leverage, 0m);
            var rawQty = currentClose > 0 ? notional / currentClose : 0m;
            if (rawQty <= 0)
            {
                _logger.LogWarning("Skipping signal {Type}: computed qty <= 0 at close {Close}.", signal.Type, currentClose);
                return null;
            }
            qty = Quantity.Create(Math.Round(rawQty, 4));
        }

        var order = Order.CreateMarketOrder(
            symbol: signal.Symbol,
            side: side,
            positionSide: positionSide,
            quantity: qty,
            strategyId: strategyId);

        await _exchange.PlaceOrderAsync(order, ct).ConfigureAwait(false);

        // 記帳：以成交均價（已含滑價）更新持倉集合 & 虛擬餘額
        var fillPrice = order.AverageFillPrice ?? Price.Create(currentClose);
        switch (signal.Type)
        {
            case SignalType.OpenLong:
            case SignalType.OpenShort:
            {
                var position = Position.Open(
                    symbol: signal.Symbol,
                    side: positionSide,
                    quantity: qty,
                    entryPrice: fillPrice,
                    leverage: strategyConfig.Leverage,
                    stopLossPrice: signal.SuggestedStopLoss,
                    takeProfitPrice: signal.SuggestedTakeProfit,
                    strategyId: strategyId);
                position.AddCommission(order.Commission);
                openPositions.Add(position);
                _logger.LogInformation(
                    "📈 [BACKTEST-OPEN] {Side} {Qty} {Symbol} @ {Price} SL={SL} TP={TP}",
                    positionSide, qty.Value, signal.Symbol.BingXFormat, fillPrice.Value,
                    signal.SuggestedStopLoss?.Value, signal.SuggestedTakeProfit?.Value);
                break;
            }

            case SignalType.CloseLong:
            case SignalType.CloseShort:
            {
                closingPosition!.Close(fillPrice, signal.Reason, order.Commission);
                openPositions.Remove(closingPosition);
                closedPositions.Add(closingPosition);
                _clock.ApplyRealizedPnL(closingPosition.RealizedPnL);
                _logger.LogInformation(
                    "📉 [BACKTEST-CLOSE] {Side} {Qty} {Symbol} @ {Price} pnl={PnL:F4}",
                    positionSide, qty.Value, signal.Symbol.BingXFormat, fillPrice.Value, closingPosition.RealizedPnL);
                break;
            }
        }

        return order;
    }

    /// <summary>
    /// 在每根 K 線 tick 時，把當根 close 當作「當前行情」餵給所有 open positions。
    /// 若 Position 因止損/止盈觸發 — <see cref="Position.UpdateCurrentPrice"/> 回傳的 Close 訊號會被
    /// 立即以當根 close 為成交價平倉，避免等到下一次策略評估才反應。
    /// </summary>
    private void TickOpenPositions(
        Kline kline,
        List<Position> openPositions,
        List<Position> closedPositions,
        Guid strategyId,
        BacktestOptions options)
    {
        if (openPositions.Count == 0) return;

        var snapshotPrice = Price.Create(kline.Close);

        // 先收集該觸發的 position，避免在 foreach 中改集合
        var toClose = new List<(Position position, SignalType signal)>();
        foreach (var p in openPositions)
        {
            var triggered = p.UpdateCurrentPrice(snapshotPrice);
            if (triggered is SignalType.CloseLong or SignalType.CloseShort)
                toClose.Add((p, triggered));
        }

        foreach (var (p, _) in toClose)
        {
            // 用簡化的手續費（不經 Simulator 重新 fill）— 止損/止盈實務上會在價位即時成交，
            // 我們用 kline.Close 做近似，後續可再加 slippage 模型
            var commission = kline.Close * p.Quantity.Value * options.CommissionRate;
            p.Close(snapshotPrice, "Stop-loss / Take-profit triggered", commission);
            openPositions.Remove(p);
            closedPositions.Add(p);
            _clock.ApplyRealizedPnL(p.RealizedPnL);
            _logger.LogInformation(
                "🛑 [BACKTEST-SLTP] {Side} {Qty} {Symbol} @ {Price} pnl={PnL:F4} ({Reason})",
                p.Side, p.Quantity.Value, p.Symbol.BingXFormat, snapshotPrice.Value, p.RealizedPnL,
                p.StopLossPrice is not null && IsStopLossHit(p, snapshotPrice) ? "SL" : "TP");
        }
    }

    private static bool IsStopLossHit(Position p, Price currentPrice) =>
        p.StopLossPrice is not null
        && (p.Side == PositionSide.Long
            ? currentPrice.Value <= p.StopLossPrice.Value
            : currentPrice.Value >= p.StopLossPrice.Value);

    private static bool IsCloseSignal(SignalType type) =>
        type is SignalType.CloseLong or SignalType.CloseShort;

    private static (OrderSide orderSide, PositionSide positionSide) MapSignalToOrderSides(SignalType type) =>
        type switch
        {
            SignalType.OpenLong   => (OrderSide.Buy,  PositionSide.Long),
            SignalType.OpenShort  => (OrderSide.Sell, PositionSide.Short),
            SignalType.CloseLong  => (OrderSide.Sell, PositionSide.Long),
            SignalType.CloseShort => (OrderSide.Buy,  PositionSide.Short),
            _ => throw new InvalidOperationException($"Unexpected signal type {type} at order mapping stage."),
        };
}
