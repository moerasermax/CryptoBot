using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;

using BxFuturesOrderType = BingX.Net.Enums.FuturesOrderType;
using BxOrderSide = BingX.Net.Enums.OrderSide;
using BxPositionSide = BingX.Net.Enums.PositionSide;
using BxMarginMode = BingX.Net.Enums.MarginMode;
using BxOrderStatus = BingX.Net.Enums.OrderStatus;
using BxKlineInterval = BingX.Net.Enums.KlineInterval;

namespace CryptoBot.Infrastructure.Exchange.BingX;

internal static class BingXMapper
{
    public static BxOrderSide ToBingX(this OrderSide side) => side switch
    {
        OrderSide.Buy => BxOrderSide.Buy,
        OrderSide.Sell => BxOrderSide.Sell,
        _ => throw new DomainException($"Unsupported OrderSide: {side}")
    };

    public static OrderSide ToDomain(this BxOrderSide side) => side switch
    {
        BxOrderSide.Buy => OrderSide.Buy,
        BxOrderSide.Sell => OrderSide.Sell,
        _ => throw new DomainException($"Unmapped BingX OrderSide: {side}")
    };

    public static BxPositionSide ToBingX(this PositionSide side) => side switch
    {
        PositionSide.Long => BxPositionSide.Long,
        PositionSide.Short => BxPositionSide.Short,
        _ => throw new DomainException($"Unsupported PositionSide: {side}")
    };

    public static PositionSide ToDomain(this BxPositionSide side) => side switch
    {
        BxPositionSide.Long => PositionSide.Long,
        BxPositionSide.Short => PositionSide.Short,
        _ => throw new DomainException($"Unmapped BingX PositionSide: {side}")
    };

    public static BxFuturesOrderType ToBingX(this OrderType type) => type switch
    {
        OrderType.Market => BxFuturesOrderType.Market,
        OrderType.Limit => BxFuturesOrderType.Limit,
        OrderType.StopMarket => BxFuturesOrderType.StopMarket,
        OrderType.StopLimit => BxFuturesOrderType.StopLimit,
        OrderType.TakeProfitMarket => BxFuturesOrderType.TakeProfitMarket,
        OrderType.TakeProfitLimit => BxFuturesOrderType.TakeProfitLimit,
        _ => throw new DomainException($"Unsupported OrderType: {type}")
    };

    public static OrderStatus ToDomain(this BxOrderStatus status)
    {
        var name = status.ToString();
        return name switch
        {
            "New" or "Pending" or "Working" => OrderStatus.New,
            "PartiallyFilled" => OrderStatus.PartiallyFilled,
            "Filled" => OrderStatus.Filled,
            "Canceled" or "Cancelled" => OrderStatus.Canceled,
            "Rejected" or "Failed" => OrderStatus.Rejected,
            "Expired" => OrderStatus.Expired,
            _ => throw new DomainException($"Unmapped BingX OrderStatus: {name}")
        };
    }

    public static BxMarginMode ToBingX(this MarginMode mode) => mode switch
    {
        MarginMode.Isolated => BxMarginMode.Isolated,
        MarginMode.Cross => BxMarginMode.Cross,
        _ => throw new DomainException($"Unsupported MarginMode: {mode}")
    };

    public static MarginMode ToDomain(this BxMarginMode mode) => mode switch
    {
        BxMarginMode.Isolated => MarginMode.Isolated,
        BxMarginMode.Cross => MarginMode.Cross,
        _ => throw new DomainException($"Unmapped BingX MarginMode: {mode}")
    };

    public static BxKlineInterval ToBingX(this KlineInterval interval) => interval switch
    {
        KlineInterval.OneMinute => BxKlineInterval.OneMinute,
        KlineInterval.ThreeMinutes => BxKlineInterval.ThreeMinutes,
        KlineInterval.FiveMinutes => BxKlineInterval.FiveMinutes,
        KlineInterval.FifteenMinutes => BxKlineInterval.FifteenMinutes,
        KlineInterval.ThirtyMinutes => BxKlineInterval.ThirtyMinutes,
        KlineInterval.OneHour => BxKlineInterval.OneHour,
        KlineInterval.TwoHours => BxKlineInterval.TwoHours,
        KlineInterval.FourHours => BxKlineInterval.FourHours,
        KlineInterval.SixHours => BxKlineInterval.SixHours,
        KlineInterval.EightHours => BxKlineInterval.EightHours,
        KlineInterval.TwelveHours => BxKlineInterval.TwelveHours,
        KlineInterval.OneDay => BxKlineInterval.OneDay,
        KlineInterval.ThreeDays => BxKlineInterval.ThreeDay,
        KlineInterval.OneWeek => BxKlineInterval.OneWeek,
        KlineInterval.OneMonth => BxKlineInterval.OneMonth,
        _ => throw new DomainException($"Unsupported KlineInterval for BingX: {interval}")
    };
}