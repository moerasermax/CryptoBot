using CryptoBot.Domain.Common;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Events;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Aggregates.PositionAggregate;

/// <summary>
/// 持倉 Aggregate Root - 合約交易的核心
/// 
/// 負責：
/// - 管理持倉的開/平/加減倉
/// - 計算未實現盈虧 (Unrealized PnL)
/// - 追蹤止損 (Stop Loss) 與止盈 (Take Profit) 觸發條件
/// - 計算強平價 (Liquidation Price) 風險
/// 
/// 不變式：
/// - 持倉數量必須 > 0 (已平倉則此 Aggregate 已結束)
/// - 做多止損價 < 入場價 < 止盈價
/// - 做空止盈價 < 入場價 < 止損價
/// </summary>
public sealed class Position : AggregateRoot<Guid>
{
    public Symbol Symbol { get; private set; }
    public PositionSide Side { get; private set; }
    public Quantity Quantity { get; private set; }
    public Price EntryPrice { get; private set; }
    public Price? CurrentPrice { get; private set; }
    public Leverage Leverage { get; private set; }
    public MarginMode MarginMode { get; private set; }

    /// <summary>止損價</summary>
    public Price? StopLossPrice { get; private set; }

    /// <summary>止盈價</summary>
    public Price? TakeProfitPrice { get; private set; }

    /// <summary>追蹤停損距離 (百分比, 0.02 = 2%)</summary>
    public decimal? TrailingStopPercent { get; private set; }

    /// <summary>追蹤停損目前的停損價 (會隨行情移動)</summary>
    public Price? TrailingStopPrice { get; private set; }

    public Guid? StrategyId { get; private set; }
    public DateTime OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public bool IsClosed { get; private set; }

    /// <summary>已實現盈虧 (平倉後填入)</summary>
    public decimal RealizedPnL { get; private set; }

    /// <summary>累計手續費</summary>
    public decimal TotalCommission { get; private set; }

    // ===== S39 交易歷史 / AI 複盤用欄位 =====

    /// <summary>
    /// 平倉成交價 — Close() 時落地的快照，與 CurrentPrice 分開儲存
    /// 目的是讓歷史報表有一個永遠等於「這筆交易的出場價」的欄位，
    /// 不會被任何後續 UpdateCurrentPrice 覆寫（理論上不會發生，但語意分離更乾淨）。
    /// </summary>
    public Price? ExitPrice { get; private set; }

    /// <summary>
    /// 開倉時鎖定的策略類型 — 例如 "SmaCrossover" / "B46RsiBb"。
    /// 存字串而不是 Guid，是為了「即使 Strategy Aggregate 後來改名或換型，
    /// 歷史交易仍能顯示下單當下使用的決策大腦」。
    /// </summary>
    public string? StrategyType { get; private set; }

    /// <summary>
    /// 開倉當下策略參數的 JSON 快照（含 Parameters dict + 風控/槓桿/止損止盈 %）。
    /// 給 AI 複盤用 — 複盤時必須知道「這筆交易用的是哪一組參數」，
    /// 才能做策略表現分析與網格比對。
    /// </summary>
    public string? ParametersSnapshot { get; private set; }

    // ===== 計算屬性 =====

    /// <summary>未實現盈虧 (需要提供當前價格)</summary>
    public decimal UnrealizedPnL
    {
        get
        {
            if (IsClosed || CurrentPrice is null) return 0;
            var diff = CurrentPrice.Value - EntryPrice.Value;
            return Side == PositionSide.Long
                ? diff * Quantity.Value
                : -diff * Quantity.Value;
        }
    }

    /// <summary>未實現盈虧百分比 (相對於保證金)</summary>
    public decimal UnrealizedPnLPercent
    {
        get
        {
            if (EntryPrice.Value == 0) return 0;
            var priceChangePercent = CurrentPrice is null ? 0
                : (CurrentPrice.Value - EntryPrice.Value) / EntryPrice.Value;
            var signed = Side == PositionSide.Long ? priceChangePercent : -priceChangePercent;
            return signed * Leverage.Value * 100m;  // 加槓桿放大
        }
    }

    /// <summary>名義價值 (Position Value)</summary>
    public decimal NotionalValue => EntryPrice.Value * Quantity.Value;

    /// <summary>初始保證金</summary>
    public decimal InitialMargin => NotionalValue / Leverage.Value;

    /// <summary>
    /// 估算強平價 (Liquidation Price)
    /// 簡化公式（實際 BingX 有維持保證金率調整）：
    /// Long:  liqPrice = entryPrice * (1 - 1/leverage + maintMarginRate)
    /// Short: liqPrice = entryPrice * (1 + 1/leverage - maintMarginRate)
    /// </summary>
    public decimal EstimatedLiquidationPrice
    {
        get
        {
            const decimal maintenanceMarginRate = 0.005m;  // 0.5% 保守估計
            var factor = 1m / Leverage.Value - maintenanceMarginRate;
            return Side == PositionSide.Long
                ? EntryPrice.Value * (1m - factor)
                : EntryPrice.Value * (1m + factor);
        }
    }

    /// <summary>距離強平價的風險百分比 (越小越危險)</summary>
    public decimal LiquidationRiskPercent
    {
        get
        {
            if (CurrentPrice is null) return 100;
            var liq = EstimatedLiquidationPrice;
            return Side == PositionSide.Long
                ? (CurrentPrice.Value - liq) / CurrentPrice.Value * 100m
                : (liq - CurrentPrice.Value) / CurrentPrice.Value * 100m;
        }
    }

    // 私有建構子
    private Position() : base(Guid.NewGuid())
    {
        Symbol = default!;
        Quantity = Quantity.Zero;
        EntryPrice = Price.Zero;
        Leverage = Leverage.Conservative;
    }

    /// <summary>
    /// 開倉 - 建立新持倉
    /// </summary>
    public static Position Open(
        Symbol symbol,
        PositionSide side,
        Quantity quantity,
        Price entryPrice,
        Leverage leverage,
        MarginMode marginMode = MarginMode.Isolated,
        Price? stopLossPrice = null,
        Price? takeProfitPrice = null,
        Guid? strategyId = null,
        string? strategyType = null,
        string? parametersSnapshot = null)
    {
        if (quantity.Value <= 0)
            throw new DomainException("Position quantity must be positive.");
        if (entryPrice.Value <= 0)
            throw new DomainException("Entry price must be positive.");

        ValidateStopAndTarget(side, entryPrice, stopLossPrice, takeProfitPrice);

        var position = new Position
        {
            Symbol = symbol,
            Side = side,
            Quantity = quantity,
            EntryPrice = entryPrice,
            CurrentPrice = entryPrice,
            Leverage = leverage,
            MarginMode = marginMode,
            StopLossPrice = stopLossPrice,
            TakeProfitPrice = takeProfitPrice,
            StrategyId = strategyId,
            StrategyType = strategyType,
            ParametersSnapshot = parametersSnapshot,
            OpenedAt = DateTime.UtcNow,
            IsClosed = false
        };

        position.RaiseDomainEvent(new PositionOpenedEvent(
            position.Id, symbol, side, quantity, entryPrice, leverage));

        return position;
    }

    /// <summary>
    /// 更新當前價格 (由行情 WebSocket 驅動)
    /// 同時檢查止損/止盈是否觸發
    /// </summary>
    /// <returns>應觸發的訊號</returns>
    public SignalType UpdateCurrentPrice(Price currentPrice)
    {
        if (IsClosed)
            throw new DomainException("Cannot update price on closed position.");

        CurrentPrice = currentPrice;

        // 更新追蹤停損
        UpdateTrailingStop(currentPrice);

        // 檢查止損
        if (IsStopLossTriggered(currentPrice))
        {
            RaiseDomainEvent(new StopLossTriggeredEvent(
                Id, Symbol, StopLossPrice!, currentPrice));
            return Side == PositionSide.Long ? SignalType.CloseLong : SignalType.CloseShort;
        }

        // 檢查止盈
        if (IsTakeProfitTriggered(currentPrice))
        {
            RaiseDomainEvent(new TakeProfitTriggeredEvent(
                Id, Symbol, TakeProfitPrice!, currentPrice));
            return Side == PositionSide.Long ? SignalType.CloseLong : SignalType.CloseShort;
        }

        return SignalType.None;
    }

    private bool IsStopLossTriggered(Price currentPrice)
    {
        var effectiveStop = TrailingStopPrice ?? StopLossPrice;
        if (effectiveStop is null) return false;

        return Side == PositionSide.Long
            ? currentPrice.Value <= effectiveStop.Value
            : currentPrice.Value >= effectiveStop.Value;
    }

    private bool IsTakeProfitTriggered(Price currentPrice)
    {
        if (TakeProfitPrice is null) return false;

        return Side == PositionSide.Long
            ? currentPrice.Value >= TakeProfitPrice.Value
            : currentPrice.Value <= TakeProfitPrice.Value;
    }

    private void UpdateTrailingStop(Price currentPrice)
    {
        if (TrailingStopPercent is null) return;

        var percent = TrailingStopPercent.Value;
        if (Side == PositionSide.Long)
        {
            // 做多：當前價向上移動時，追蹤停損跟著上移
            var newStop = Price.Create(currentPrice.Value * (1m - percent));
            if (TrailingStopPrice is null || newStop.Value > TrailingStopPrice.Value)
                TrailingStopPrice = newStop;
        }
        else
        {
            // 做空：當前價向下移動時，追蹤停損跟著下移
            var newStop = Price.Create(currentPrice.Value * (1m + percent));
            if (TrailingStopPrice is null || newStop.Value < TrailingStopPrice.Value)
                TrailingStopPrice = newStop;
        }
    }

    /// <summary>
    /// 設定追蹤停損 (例如 0.02 = 2%)
    /// </summary>
    public void EnableTrailingStop(decimal percent)
    {
        if (IsClosed)
            throw new DomainException("Cannot set trailing stop on closed position.");
        if (percent <= 0 || percent >= 1)
            throw new DomainException("Trailing stop percent must be in (0, 1).");

        TrailingStopPercent = percent;
        if (CurrentPrice is not null)
            UpdateTrailingStop(CurrentPrice);
    }

    /// <summary>
    /// 修改止損價
    /// </summary>
    public void ModifyStopLoss(Price? newStopLoss)
    {
        if (IsClosed)
            throw new DomainException("Cannot modify closed position.");
        ValidateStopAndTarget(Side, EntryPrice, newStopLoss, TakeProfitPrice);
        StopLossPrice = newStopLoss;
    }

    /// <summary>
    /// 修改止盈價
    /// </summary>
    public void ModifyTakeProfit(Price? newTakeProfit)
    {
        if (IsClosed)
            throw new DomainException("Cannot modify closed position.");
        ValidateStopAndTarget(Side, EntryPrice, StopLossPrice, newTakeProfit);
        TakeProfitPrice = newTakeProfit;
    }

    /// <summary>
    /// 平倉
    /// </summary>
    public void Close(Price exitPrice, string reason, decimal closeCommission = 0)
    {
        if (IsClosed)
            throw new DomainException("Position is already closed.");

        var diff = exitPrice.Value - EntryPrice.Value;
        var grossPnL = Side == PositionSide.Long
            ? diff * Quantity.Value
            : -diff * Quantity.Value;

        TotalCommission += closeCommission;
        // S77 fix: TotalCommission 是 commission expense (負值，如 -44.14 = 開倉手續費 cost)。
        // net PnL 應為 grossPnL + TotalCommission（commission 已是負、相加即扣手續費）。
        // 既有 `grossPnL - TotalCommission` 是 phantom close 假宣告獲利 bug 的 root cause:
        //   phantom case grossPnL=0 + TotalCommission=-44 → 0 - (-44) = +44 (反向變獲利)
        //   真實虧損 case grossPnL=-82 + TotalCommission=-44 → -82 - (-44) = -38 (commission 反向當補貼)
        // 對齊 IRON ⑤ 風控透明化 — RealizedPnL 必須是真實 net PnL，不可符號錯。
        RealizedPnL = grossPnL + TotalCommission;
        IsClosed = true;
        ClosedAt = DateTime.UtcNow;
        CurrentPrice = exitPrice;
        ExitPrice = exitPrice;

        RaiseDomainEvent(new PositionClosedEvent(
            Id, Symbol, Side, Quantity, EntryPrice, exitPrice, RealizedPnL, reason));
    }

    /// <summary>
    /// 加倉 (Pyramiding) - 重新計算平均入場價
    /// </summary>
    public void AddToPosition(Quantity additionalQuantity, Price addPrice)
    {
        if (IsClosed)
            throw new DomainException("Cannot add to closed position.");

        var totalValue = EntryPrice.Value * Quantity.Value
                       + addPrice.Value * additionalQuantity.Value;
        var newQty = Quantity + additionalQuantity;
        EntryPrice = Price.Create(totalValue / newQty.Value);
        Quantity = newQty;
    }

    /// <summary>
    /// 減倉
    /// </summary>
    public void ReducePosition(Quantity reduceQuantity, Price reducePrice)
    {
        if (IsClosed)
            throw new DomainException("Cannot reduce closed position.");
        if (reduceQuantity.Value > Quantity.Value)
            throw new DomainException("Cannot reduce more than current quantity.");

        var diff = reducePrice.Value - EntryPrice.Value;
        var realizedOnReduction = Side == PositionSide.Long
            ? diff * reduceQuantity.Value
            : -diff * reduceQuantity.Value;
        RealizedPnL += realizedOnReduction;

        Quantity = Quantity - reduceQuantity;

        if (Quantity.Value == 0)
        {
            IsClosed = true;
            ClosedAt = DateTime.UtcNow;
            ExitPrice = reducePrice;
        }
    }

    public void AddCommission(decimal commission) => TotalCommission += commission;

    private static void ValidateStopAndTarget(
        PositionSide side, Price entry, Price? sl, Price? tp)
    {
        if (side == PositionSide.Long)
        {
            if (sl is not null && sl.Value >= entry.Value)
                throw new DomainException(
                    $"Long stop loss {sl} must be below entry {entry}");
            if (tp is not null && tp.Value <= entry.Value)
                throw new DomainException(
                    $"Long take profit {tp} must be above entry {entry}");
        }
        else
        {
            if (sl is not null && sl.Value <= entry.Value)
                throw new DomainException(
                    $"Short stop loss {sl} must be above entry {entry}");
            if (tp is not null && tp.Value >= entry.Value)
                throw new DomainException(
                    $"Short take profit {tp} must be below entry {entry}");
        }
    }
}
