using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.ValueObjects;

/// <summary>
/// 槓桿倍數。合約交易的核心概念。
/// </summary>
public sealed class Leverage : Common.ValueObject
{
    public int Value { get; }

    private Leverage(int value) => Value = value;

    /// <summary>
    /// BingX 永續合約槓桿範圍通常是 1-125 (因幣種而異)
    /// 為保守起見,我們限制最大為 20 倍
    /// </summary>
    public static Leverage Create(int value, int max = 20)
    {
        if (value < 1)
            throw new DomainException($"Leverage must be at least 1: {value}");
        if (value > max)
            throw new DomainException(
                $"Leverage {value} exceeds maximum allowed {max}. " +
                "High leverage carries extreme risk of liquidation.");
        return new Leverage(value);
    }

    public static Leverage Conservative => new(2);
    public static Leverage Moderate => new(5);
    public static Leverage Aggressive => new(10);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => $"{Value}x";

    public static implicit operator int(Leverage l) => l.Value;
}
