using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.ValueObjects;

/// <summary>
/// 價格 Value Object。
/// 使用 decimal 而非 double 以避免浮點誤差 (金融計算必須)。
/// </summary>
public sealed class Price : Common.ValueObject, IComparable<Price>
{
    public decimal Value { get; }

    private Price(decimal value) => Value = value;

    public static Price Create(decimal value)
    {
        if (value < 0)
            throw new DomainException($"Price cannot be negative: {value}");
        return new Price(value);
    }

    public static Price Zero => new(0);

    public int CompareTo(Price? other) =>
        other is null ? 1 : Value.CompareTo(other.Value);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString("0.########");

    // Implicit conversion for convenience
    public static implicit operator decimal(Price p) => p.Value;

    // Arithmetic operators
    public static Price operator +(Price a, Price b) => Create(a.Value + b.Value);
    public static Price operator -(Price a, Price b) => Create(Math.Max(0, a.Value - b.Value));
    public static Price operator *(Price p, decimal factor) => Create(p.Value * factor);
    public static Price operator /(Price p, decimal divisor) => Create(p.Value / divisor);

    public static bool operator <(Price a, Price b) => a.Value < b.Value;
    public static bool operator >(Price a, Price b) => a.Value > b.Value;
    public static bool operator <=(Price a, Price b) => a.Value <= b.Value;
    public static bool operator >=(Price a, Price b) => a.Value >= b.Value;
}
