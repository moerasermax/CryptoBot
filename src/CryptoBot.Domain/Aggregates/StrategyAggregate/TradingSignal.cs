using CryptoBot.Domain.Common;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Aggregates.StrategyAggregate;

/// <summary>
/// 交易訊號 Value Object - 策略的輸出
/// </summary>
public sealed class TradingSignal : ValueObject
{
    public SignalType Type { get; }
    public Symbol Symbol { get; }
    public Price SuggestedPrice { get; }
    public Price? SuggestedStopLoss { get; }
    public Price? SuggestedTakeProfit { get; }

    /// <summary>訊號強度 0.0 ~ 1.0 (用於決定倉位大小)</summary>
    public decimal Confidence { get; }

    public string Reason { get; }
    public DateTime GeneratedAt { get; }

    private TradingSignal(
        SignalType type, Symbol symbol, Price suggestedPrice,
        Price? suggestedStopLoss, Price? suggestedTakeProfit,
        decimal confidence, string reason)
    {
        Type = type;
        Symbol = symbol;
        SuggestedPrice = suggestedPrice;
        SuggestedStopLoss = suggestedStopLoss;
        SuggestedTakeProfit = suggestedTakeProfit;
        Confidence = Math.Clamp(confidence, 0m, 1m);
        Reason = reason;
        GeneratedAt = DateTime.UtcNow;
    }

    public static TradingSignal None(Symbol symbol, Price currentPrice) =>
        new(SignalType.None, symbol, currentPrice, null, null, 0, "No signal");

    public static TradingSignal OpenLong(
        Symbol symbol, Price entry, Price? stopLoss, Price? takeProfit,
        decimal confidence, string reason) =>
        new(SignalType.OpenLong, symbol, entry, stopLoss, takeProfit, confidence, reason);

    public static TradingSignal OpenShort(
        Symbol symbol, Price entry, Price? stopLoss, Price? takeProfit,
        decimal confidence, string reason) =>
        new(SignalType.OpenShort, symbol, entry, stopLoss, takeProfit, confidence, reason);

    public static TradingSignal CloseLong(Symbol symbol, Price exit, string reason) =>
        new(SignalType.CloseLong, symbol, exit, null, null, 1.0m, reason);

    public static TradingSignal CloseShort(Symbol symbol, Price exit, string reason) =>
        new(SignalType.CloseShort, symbol, exit, null, null, 1.0m, reason);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Type;
        yield return Symbol;
        yield return GeneratedAt;
    }

    public override string ToString() =>
        $"{Type} {Symbol} @ {SuggestedPrice} (conf={Confidence:P0}) - {Reason}";
}
