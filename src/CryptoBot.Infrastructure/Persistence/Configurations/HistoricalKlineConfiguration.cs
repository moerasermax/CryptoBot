using CryptoBot.Infrastructure.Backtesting.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoBot.Infrastructure.Persistence.Configurations;

/// <summary>
/// 回測用歷史 K 線表。複合主鍵 (Symbol, Interval, OpenTime)。
///
/// 為什麼不直接重用 Order/Position 那種 int Id + Index：
/// - Upsert 時需要以 (Symbol, Interval, OpenTime) 去做冪等判定，拆成複合主鍵語意最直接。
/// - 回測讀取必為區間查詢，前綴索引 (Symbol, Interval) + 主鍵時間自然遞增即是最佳 range scan。
/// </summary>
public sealed class HistoricalKlineConfiguration : IEntityTypeConfiguration<HistoricalKlineRecord>
{
    public void Configure(EntityTypeBuilder<HistoricalKlineRecord> b)
    {
        b.ToTable("HistoricalKlines");

        b.HasKey(k => new { k.Symbol, k.Interval, k.OpenTime });

        b.Property(k => k.Symbol).HasMaxLength(32).IsRequired();
        b.Property(k => k.Interval).HasConversion<int>().IsRequired();
        b.Property(k => k.OpenTime).IsRequired();
        b.Property(k => k.CloseTime).IsRequired();

        b.Property(k => k.Open).HasPrecision(28, 12).IsRequired();
        b.Property(k => k.High).HasPrecision(28, 12).IsRequired();
        b.Property(k => k.Low).HasPrecision(28, 12).IsRequired();
        b.Property(k => k.Close).HasPrecision(28, 12).IsRequired();
        b.Property(k => k.Volume).HasPrecision(28, 12).IsRequired();
    }
}
