namespace CryptoBot.Domain.Common;

/// <summary>
/// 所有實體 (Entity) 的抽象基類。
/// Entity 在 DDD 中定義為具有唯一識別碼且生命週期中保持身份一致的物件。
/// </summary>
/// <typeparam name="TId">識別碼類型</typeparam>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    public TId Id { get; protected set; }

    protected Entity(TId id)
    {
        if (id is null)
            throw new ArgumentNullException(nameof(id));
        Id = id;
    }

    // EF Core 或序列化用
    protected Entity() => Id = default!;

    public bool Equals(Entity<TId>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return Id.Equals(other.Id);
    }

    public override bool Equals(object? obj) => obj is Entity<TId> e && Equals(e);

    public override int GetHashCode() => Id.GetHashCode();

    public static bool operator ==(Entity<TId>? a, Entity<TId>? b) =>
        a is null ? b is null : a.Equals(b);

    public static bool operator !=(Entity<TId>? a, Entity<TId>? b) => !(a == b);
}
