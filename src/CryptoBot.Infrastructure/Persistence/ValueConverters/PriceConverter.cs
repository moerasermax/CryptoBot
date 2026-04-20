using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CryptoBot.Infrastructure.Persistence.ValueConverters;

/// <summary>
/// Price ↔ decimal。Nullable Price? 屬性 EF 會自動處理 NULL（存 NULL，讀回 null）。
/// </summary>
public sealed class PriceConverter : ValueConverter<Price, decimal>
{
    public PriceConverter()
        : base(
            v => v.Value,
            v => Price.Create(v))
    {
    }
}
