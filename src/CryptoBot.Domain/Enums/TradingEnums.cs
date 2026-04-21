namespace CryptoBot.Domain.Enums;

/// <summary>
/// 持倉方向
/// </summary>
public enum PositionSide
{
    Long = 1,   // 做多
    Short = 2   // 做空
}

/// <summary>
/// 訂單方向
/// </summary>
public enum OrderSide
{
    Buy = 1,
    Sell = 2
}

/// <summary>
/// 訂單類型
/// </summary>
public enum OrderType
{
    Market = 1,         // 市價單
    Limit = 2,          // 限價單
    StopMarket = 3,     // 止損市價單
    StopLimit = 4,      // 止損限價單
    TakeProfitMarket = 5,
    TakeProfitLimit = 6
}

/// <summary>
/// 訂單狀態
/// </summary>
public enum OrderStatus
{
    New = 1,            // 新建
    PartiallyFilled = 2, // 部分成交
    Filled = 3,          // 已成交
    Canceled = 4,        // 已取消
    Rejected = 5,        // 已拒絕
    Expired = 6          // 已過期
}

/// <summary>
/// 持倉模式
/// </summary>
public enum PositionMode
{
    OneWay = 1,     // 單向持倉
    Hedge = 2       // 雙向持倉（對沖模式）
}

/// <summary>
/// 保證金模式
/// </summary>
public enum MarginMode
{
    Isolated = 1,   // 逐倉
    Cross = 2       // 全倉
}

/// <summary>
/// K 線時間週期
/// </summary>
public enum KlineInterval
{
    OneMinute,
    ThreeMinutes,
    FiveMinutes,
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    TwoHours,
    FourHours,
    SixHours,
    EightHours,
    TwelveHours,
    OneDay,
    ThreeDays,
    OneWeek,
    OneMonth
}

/// <summary>
/// 交易訊號類型
/// </summary>
public enum SignalType
{
    None = 0,
    OpenLong = 1,   // 開多
    OpenShort = 2,  // 開空
    CloseLong = 3,  // 平多
    CloseShort = 4  // 平空
}

/// <summary>
/// 策略狀態
/// </summary>
public enum StrategyStatus
{
    Stopped = 0,
    Running = 1,
    Paused = 2,
    Error = 3
}

/// <summary>
/// 交易所識別。預留 Binance/OKX/Bybit 給未來多交易所支援。
/// </summary>
public enum ExchangeName
{
    BingX = 1,
    Binance = 2,
    OKX = 3,
    Bybit = 4
}
