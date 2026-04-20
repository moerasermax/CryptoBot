using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CryptoBot.Infrastructure.Persistence.ValueConverters;

public sealed class QuantityConverter : ValueConverter<Quantity, decimal>
{
    public QuantityConverter()
        : base(
            v => v.Value,
            v => Quantity.Create(v))
    {
    }
}
