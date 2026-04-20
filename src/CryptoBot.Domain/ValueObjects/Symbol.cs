using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.ValueObjects;

/// <summary>
/// 交易對 (Trading Symbol) Value Object
/// 例如: BTC-USDT, ETH-USDT
/// 這是整個系統靈活切換幣種的核心，所有領域物件透過此 VO 來引用交易對。
/// </summary>
public sealed class Symbol : Common.ValueObject
{
    /// <summary>基礎資產 (如 BTC)</summary>
    public string BaseAsset { get; }

    /// <summary>計價資產 (如 USDT)</summary>
    public string QuoteAsset { get; }

    /// <summary>BingX 格式 (如 BTC-USDT)</summary>
    public string BingXFormat => $"{BaseAsset}-{QuoteAsset}";

    /// <summary>標準格式 (如 BTCUSDT)</summary>
    public string StandardFormat => $"{BaseAsset}{QuoteAsset}";

    private Symbol(string baseAsset, string quoteAsset)
    {
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
    }

    public static Symbol Create(string baseAsset, string quoteAsset)
    {
        if (string.IsNullOrWhiteSpace(baseAsset))
            throw new DomainException("Base asset cannot be empty.");
        if (string.IsNullOrWhiteSpace(quoteAsset))
            throw new DomainException("Quote asset cannot be empty.");

        return new Symbol(baseAsset.Trim().ToUpperInvariant(),
                          quoteAsset.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// 從字串解析,支援 "BTC-USDT" 或 "BTCUSDT" 格式
    /// </summary>
    public static Symbol Parse(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new DomainException("Symbol cannot be empty.");

        var normalized = symbol.Trim().ToUpperInvariant();

        // 處理 BTC-USDT 格式
        if (normalized.Contains('-'))
        {
            var parts = normalized.Split('-');
            if (parts.Length != 2)
                throw new DomainException($"Invalid symbol format: {symbol}");
            return Create(parts[0], parts[1]);
        }

        // 處理 BTCUSDT 格式 (嘗試用常見計價資產切分)
        string[] commonQuotes = { "USDT", "USDC", "BUSD", "BTC", "ETH" };
        foreach (var quote in commonQuotes)
        {
            if (normalized.EndsWith(quote) && normalized.Length > quote.Length)
            {
                var baseAsset = normalized[..^quote.Length];
                return Create(baseAsset, quote);
            }
        }

        throw new DomainException($"Cannot parse symbol: {symbol}");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return BaseAsset;
        yield return QuoteAsset;
    }

    public override string ToString() => BingXFormat;
}
