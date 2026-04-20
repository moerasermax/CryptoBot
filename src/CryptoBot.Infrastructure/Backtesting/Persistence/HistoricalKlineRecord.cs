using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Infrastructure.Backtesting.Persistence;

/// <summary>
/// 歷史 K 線的持久化表述。
///
/// 與 Domain 的 <see cref="Kline"/> 值物件分離：
/// - Kline 是不可變值物件，且相等性只看 (OpenTime, Interval)；放進 DbSet 會因為無主鍵而讓 EF Core 無法操作。
/// - 這個紀錄型別擁有明確複合主鍵 (Symbol, Interval, OpenTime)，承擔儲存、查詢、覆寫職責。
/// 兩者透過 <see cref="FromKline"/> / <see cref="ToKline"/> 進行轉換。
/// </summary>
public sealed class HistoricalKlineRecord
{
    public string Symbol { get; private set; } = string.Empty;
    public KlineInterval Interval { get; private set; }
    public DateTime OpenTime { get; private set; }
    public DateTime CloseTime { get; private set; }
    public decimal Open { get; private set; }
    public decimal High { get; private set; }
    public decimal Low { get; private set; }
    public decimal Close { get; private set; }
    public decimal Volume { get; private set; }

    private HistoricalKlineRecord() { }

    public static HistoricalKlineRecord FromKline(Symbol symbol, Kline kline) => new()
    {
        Symbol = symbol.BingXFormat,
        Interval = kline.Interval,
        OpenTime = kline.OpenTime,
        CloseTime = kline.CloseTime,
        Open = kline.Open,
        High = kline.High,
        Low = kline.Low,
        Close = kline.Close,
        Volume = kline.Volume,
    };

    public Kline ToKline() => Kline.Create(
        openTime: OpenTime,
        closeTime: CloseTime,
        open: Open,
        high: High,
        low: Low,
        close: Close,
        volume: Volume,
        interval: Interval);
}
