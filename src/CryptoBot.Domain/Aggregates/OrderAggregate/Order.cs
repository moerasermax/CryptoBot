using CryptoBot.Domain.Common;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Events;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Aggregates.OrderAggregate;

/// <summary>
/// 訂單 Aggregate Root。
/// 
/// 負責：
/// - 管理訂單的完整生命週期 (New → PartiallyFilled → Filled/Canceled/Rejected)
/// - 確保狀態轉換的合法性 (state machine)
/// - 追蹤成交情況
/// - 發布相關 Domain Events
/// 
/// 不變式 (Invariants):
/// - FilledQuantity 永遠 <= Quantity
/// - 已終結狀態 (Filled/Canceled/Rejected) 不可再變更
/// - LimitPrice 對 Limit 類訂單必填
/// </summary>
public sealed class Order : AggregateRoot<Guid>
{
    public Symbol Symbol { get; private set; }
    public OrderSide Side { get; private set; }
    public OrderType Type { get; private set; }
    public Quantity Quantity { get; private set; }
    public Quantity FilledQuantity { get; private set; }
    public Price? LimitPrice { get; private set; }
    public Price? StopPrice { get; private set; }
    public Price? AverageFillPrice { get; private set; }
    public OrderStatus Status { get; private set; }
    public PositionSide PositionSide { get; private set; }
    public decimal Commission { get; private set; }
    public string? ClientOrderId { get; private set; }

    /// <summary>交易所給的訂單 ID (下單後才會有)</summary>
    public string? ExchangeOrderId { get; private set; }

    /// <summary>關聯的策略 ID (用來追蹤是哪個策略下的單)</summary>
    public Guid? StrategyId { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public string? RejectReason { get; private set; }

    // 計算屬性
    public bool IsActive => Status == OrderStatus.New || Status == OrderStatus.PartiallyFilled;
    public bool IsFinalized => Status == OrderStatus.Filled
                            || Status == OrderStatus.Canceled
                            || Status == OrderStatus.Rejected
                            || Status == OrderStatus.Expired;
    public Quantity RemainingQuantity => Quantity - FilledQuantity;

    // 私有建構子 - 只能透過工廠方法建立
    private Order() : base(Guid.NewGuid())
    {
        Symbol = default!;
        Quantity = Quantity.Zero;
        FilledQuantity = Quantity.Zero;
    }

    /// <summary>
    /// 建立限價單 (Limit Order)
    /// </summary>
    public static Order CreateLimitOrder(
        Symbol symbol,
        OrderSide side,
        PositionSide positionSide,
        Quantity quantity,
        Price limitPrice,
        Guid? strategyId = null,
        string? clientOrderId = null)
    {
        if (quantity.Value <= 0)
            throw new DomainException("Order quantity must be positive.");
        if (limitPrice.Value <= 0)
            throw new DomainException("Limit price must be positive.");

        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            PositionSide = positionSide,
            Type = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = limitPrice,
            Status = OrderStatus.New,
            StrategyId = strategyId,
            ClientOrderId = clientOrderId ?? GenerateClientOrderId(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        order.RaiseDomainEvent(new OrderPlacedEvent(
            order.Id, symbol, side, OrderType.Limit, quantity, limitPrice));

        return order;
    }

    /// <summary>
    /// 建立市價單 (Market Order)
    /// </summary>
    public static Order CreateMarketOrder(
        Symbol symbol,
        OrderSide side,
        PositionSide positionSide,
        Quantity quantity,
        Guid? strategyId = null,
        string? clientOrderId = null)
    {
        if (quantity.Value <= 0)
            throw new DomainException("Order quantity must be positive.");

        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            PositionSide = positionSide,
            Type = OrderType.Market,
            Quantity = quantity,
            Status = OrderStatus.New,
            StrategyId = strategyId,
            ClientOrderId = clientOrderId ?? GenerateClientOrderId(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        order.RaiseDomainEvent(new OrderPlacedEvent(
            order.Id, symbol, side, OrderType.Market, quantity, null));

        return order;
    }

    /// <summary>
    /// 建立止損單 (Stop Market Order) - 合約風控必備
    /// </summary>
    public static Order CreateStopMarketOrder(
        Symbol symbol,
        OrderSide side,
        PositionSide positionSide,
        Quantity quantity,
        Price stopPrice,
        Guid? strategyId = null)
    {
        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            PositionSide = positionSide,
            Type = OrderType.StopMarket,
            Quantity = quantity,
            StopPrice = stopPrice,
            Status = OrderStatus.New,
            StrategyId = strategyId,
            ClientOrderId = GenerateClientOrderId(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        order.RaiseDomainEvent(new OrderPlacedEvent(
            order.Id, symbol, side, OrderType.StopMarket, quantity, null));
        return order;
    }

    /// <summary>
    /// 綁定交易所回傳的訂單 ID
    /// </summary>
    public void AssignExchangeOrderId(string exchangeOrderId)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
            throw new DomainException("Exchange order ID cannot be empty.");
        if (ExchangeOrderId is not null)
            throw new DomainException(
                $"Exchange order ID already assigned: {ExchangeOrderId}");

        ExchangeOrderId = exchangeOrderId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// 訂單成交（全部或部分）
    /// </summary>
    public void RecordFill(Quantity filledQty, Price fillPrice, decimal commission)
    {
        EnsureNotFinalized();

        if (filledQty.Value <= 0)
            throw new DomainException("Filled quantity must be positive.");

        var newFilledQty = FilledQuantity + filledQty;
        if (newFilledQty.Value > Quantity.Value)
            throw new DomainException(
                $"Cannot fill {newFilledQty} exceeding order quantity {Quantity}");

        // 計算平均成交價 (加權平均)
        if (AverageFillPrice is null)
        {
            AverageFillPrice = fillPrice;
        }
        else
        {
            var totalValue = AverageFillPrice.Value * FilledQuantity.Value
                           + fillPrice.Value * filledQty.Value;
            AverageFillPrice = Price.Create(totalValue / newFilledQty.Value);
        }

        FilledQuantity = newFilledQty;
        Commission += commission;
        UpdatedAt = DateTime.UtcNow;

        // 狀態轉換
        if (FilledQuantity.Value >= Quantity.Value)
        {
            Status = OrderStatus.Filled;
            RaiseDomainEvent(new OrderFilledEvent(
                Id, Symbol, Side, FilledQuantity, AverageFillPrice!, Commission));
        }
        else
        {
            Status = OrderStatus.PartiallyFilled;
        }
    }

    /// <summary>
    /// 取消訂單
    /// </summary>
    public void Cancel(string reason)
    {
        EnsureNotFinalized();

        Status = OrderStatus.Canceled;
        UpdatedAt = DateTime.UtcNow;
        RaiseDomainEvent(new OrderCanceledEvent(Id, Symbol, reason));
    }

    /// <summary>
    /// 訂單被交易所拒絕
    /// </summary>
    public void Reject(string reason)
    {
        EnsureNotFinalized();

        Status = OrderStatus.Rejected;
        RejectReason = reason;
        UpdatedAt = DateTime.UtcNow;
        RaiseDomainEvent(new OrderRejectedEvent(Id, Symbol, reason));
    }

    /// <summary>
    /// 標記為過期
    /// </summary>
    public void Expire()
    {
        EnsureNotFinalized();
        Status = OrderStatus.Expired;
        UpdatedAt = DateTime.UtcNow;
    }

    private void EnsureNotFinalized()
    {
        if (IsFinalized)
            throw new DomainException(
                $"Cannot modify order {Id} in finalized state {Status}");
    }

    private static string GenerateClientOrderId() =>
        $"cb_{Guid.NewGuid():N}"[..20];  // 縮短至 20 字以符合多數交易所限制
}
