using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CryptoBot.Infrastructure.Persistence.ValueConverters;

public sealed class LeverageConverter : ValueConverter<Leverage, int>
{
    public LeverageConverter()
        : base(
            v => v.Value,
            v => Leverage.Create(v))
    {
    }
}
