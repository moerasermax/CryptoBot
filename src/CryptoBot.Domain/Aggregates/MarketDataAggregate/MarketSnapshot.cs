using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Aggregates.MarketDataAggregate;

/// <summary>
/// 市場快照 - 某個時刻的市場狀態
/// 對沖套利策略需要同時觀察現貨與合約價格
/// </summary>
public sealed class MarketSnapshot : Common.ValueObject
{
    public Symbol Symbol { get; }
    public DateTime Timestamp { get; }

    /// <summary>永續合約最新價</summary>
    public Price FuturesMarkPrice { get; }

    /// <summary>永續合約最優買價</summary>
    public Price FuturesBidPrice { get; }

    /// <summary>永續合約最優賣價</summary>
    public Price FuturesAskPrice { get; }

    /// <summary>現貨最新價 (可為 null, 若未訂閱現貨)</summary>
    public Price? SpotPrice { get; }

    /// <summary>資金費率 (永續合約特有)</summary>
    public decimal? FundingRate { get; }

    /// <summary>合約-現貨價差 (基差)</summary>
    public decimal? Basis => SpotPrice is null ? null : FuturesMarkPrice.Value - SpotPrice.Value;

    /// <summary>基差百分比</summary>
    public decimal? BasisPercent =>
        SpotPrice is null || SpotPrice.Value == 0 ? null
            : (FuturesMarkPrice.Value - SpotPrice.Value) / SpotPrice.Value * 100m;

    /// <summary>買賣價差</summary>
    public decimal Spread => FuturesAskPrice.Value - FuturesBidPrice.Value;

    private MarketSnapshot(
        Symbol symbol, DateTime timestamp,
        Price futuresMarkPrice, Price futuresBidPrice, Price futuresAskPrice,
        Price? spotPrice, decimal? fundingRate)
    {
        Symbol = symbol;
        Timestamp = timestamp;
        FuturesMarkPrice = futuresMarkPrice;
        FuturesBidPrice = futuresBidPrice;
        FuturesAskPrice = futuresAskPrice;
        SpotPrice = spotPrice;
        FundingRate = fundingRate;
    }

    public static MarketSnapshot Create(
        Symbol symbol, DateTime timestamp,
        Price futuresMarkPrice, Price futuresBidPrice, Price futuresAskPrice,
        Price? spotPrice = null, decimal? fundingRate = null)
        => new(symbol, timestamp, futuresMarkPrice, futuresBidPrice, futuresAskPrice,
               spotPrice, fundingRate);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Symbol;
        yield return Timestamp;
        yield return FuturesMarkPrice;
    }
}
