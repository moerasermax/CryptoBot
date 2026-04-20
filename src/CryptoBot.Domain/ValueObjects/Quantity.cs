using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.ValueObjects;

/// <summary>
/// 數量 Value Object (合約張數或幣數)
/// </summary>
public sealed class Quantity : Common.ValueObject, IComparable<Quantity>
{
    public decimal Value { get; }

    private Quantity(decimal value) => Value = value;

    public static Quantity Create(decimal value)
    {
        if (value < 0)
            throw new DomainException($"Quantity cannot be negative: {value}");
        return new Quantity(value);
    }

    public static Quantity Zero => new(0);

    public int CompareTo(Quantity? other) =>
        other is null ? 1 : Value.CompareTo(other.Value);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString("0.########");

    public static implicit operator decimal(Quantity q) => q.Value;

    public static Quantity operator +(Quantity a, Quantity b) => Create(a.Value + b.Value);
    public static Quantity operator -(Quantity a, Quantity b) => Create(Math.Max(0, a.Value - b.Value));
    public static Quantity operator *(Quantity q, decimal factor) => Create(q.Value * factor);

    public static bool operator <(Quantity a, Quantity b) => a.Value < b.Value;
    public static bool operator >(Quantity a, Quantity b) => a.Value > b.Value;
}
