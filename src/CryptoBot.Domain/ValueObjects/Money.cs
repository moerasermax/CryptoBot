using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.ValueObjects;

/// <summary>
/// 資金 Value Object (帶幣種)
/// </summary>
public sealed class Money : Common.ValueObject
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Create(decimal amount, string currency = "USDT")
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("Currency cannot be empty.");
        return new Money(amount, currency.ToUpperInvariant());
    }

    public static Money Zero(string currency = "USDT") => new(0, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new DomainException(
                $"Cannot operate on different currencies: {Currency} vs {other.Currency}");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => $"{Amount:0.########} {Currency}";
}
