using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CryptoBot.Infrastructure.Persistence.ValueConverters;

/// <summary>
/// Price? ↔ decimal?。專供 nullable Price 屬性使用，
/// 避免 ValueConverter&lt;Price, decimal&gt; 在 nullable 場景下的 CS8620 變異性警告。
/// EF Core 會自動把 null 對應到 SQL NULL。
/// </summary>
public sealed class NullablePriceConverter : ValueConverter<Price?, decimal?>
{
    public NullablePriceConverter()
        : base(
            v => v == null ? (decimal?)null : v.Value,
            v => v == null ? null : Price.Create(v.Value))
    {
    }
}
