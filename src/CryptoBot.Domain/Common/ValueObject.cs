namespace CryptoBot.Domain.Common;

/// <summary>
/// 值物件 (Value Object) 抽象基類。
/// Value Object 的相等性基於其所有屬性值，而非身份。
/// 特性：不可變 (Immutable)、可替換 (Replaceable)、自我驗證 (Self-validating)。
/// </summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>
    /// 由子類提供用於相等性比較的所有組成屬性
    /// </summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override bool Equals(object? obj) => obj is ValueObject vo && Equals(vo);

    public override int GetHashCode()
    {
        return GetEqualityComponents()
            .Aggregate(1, (hash, obj) =>
                HashCode.Combine(hash, obj?.GetHashCode() ?? 0));
    }

    public static bool operator ==(ValueObject? a, ValueObject? b) =>
        a is null ? b is null : a.Equals(b);

    public static bool operator !=(ValueObject? a, ValueObject? b) => !(a == b);
}
