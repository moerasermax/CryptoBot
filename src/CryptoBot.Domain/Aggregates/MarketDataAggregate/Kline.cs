using CryptoBot.Domain.Enums;

namespace CryptoBot.Domain.Aggregates.MarketDataAggregate;

/// <summary>
/// K 線數據 (Candlestick) - 作為不可變的 Value Object
/// 技術分析的基本單位
/// </summary>
public sealed class Kline : Common.ValueObject
{
    public DateTime OpenTime { get; }
    public DateTime CloseTime { get; }
    public decimal Open { get; }
    public decimal High { get; }
    public decimal Low { get; }
    public decimal Close { get; }
    public decimal Volume { get; }
    public KlineInterval Interval { get; }

    // 領域邏輯 - 封裝常用計算
    public bool IsBullish => Close > Open;
    public bool IsBearish => Close < Open;
    public decimal Range => High - Low;
    public decimal BodySize => Math.Abs(Close - Open);
    public decimal UpperShadow => High - Math.Max(Open, Close);
    public decimal LowerShadow => Math.Min(Open, Close) - Low;

    /// <summary>
    /// 典型價格 (HLC/3) - 常用於某些技術指標
    /// </summary>
    public decimal TypicalPrice => (High + Low + Close) / 3m;

    private Kline(
        DateTime openTime, DateTime closeTime,
        decimal open, decimal high, decimal low, decimal close,
        decimal volume, KlineInterval interval)
    {
        OpenTime = openTime;
        CloseTime = closeTime;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        Interval = interval;
    }

    public static Kline Create(
        DateTime openTime, DateTime closeTime,
        decimal open, decimal high, decimal low, decimal close,
        decimal volume, KlineInterval interval)
    {
        if (high < low)
            throw new Exceptions.DomainException("Kline high cannot be less than low.");
        if (high < open || high < close)
            throw new Exceptions.DomainException("Kline high must be >= open and close.");
        if (low > open || low > close)
            throw new Exceptions.DomainException("Kline low must be <= open and close.");
        if (volume < 0)
            throw new Exceptions.DomainException("Volume cannot be negative.");
        if (closeTime <= openTime)
            throw new Exceptions.DomainException("Close time must be after open time.");

        return new Kline(openTime, closeTime, open, high, low, close, volume, interval);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return OpenTime;
        yield return Interval;
    }

    public override string ToString() =>
        $"K[{OpenTime:yyyy-MM-dd HH:mm}] O={Open} H={High} L={Low} C={Close} V={Volume}";
}
