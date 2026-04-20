using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Domain.Tests;

public class SanityTests
{
    [Fact]
    public void Symbol_Parse_HyphenForm_ReturnsExpectedAssets()
    {
        var symbol = Symbol.Parse("BTC-USDT");

        Assert.Equal("BTC", symbol.BaseAsset);
        Assert.Equal("USDT", symbol.QuoteAsset);
        Assert.Equal("BTC-USDT", symbol.BingXFormat);
    }
}
