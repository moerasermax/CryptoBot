using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Infrastructure.Persistence.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainStrategy = CryptoBot.Domain.Aggregates.StrategyAggregate.Strategy;

namespace CryptoBot.Infrastructure.Persistence.Configurations;

/// <summary>
/// 注意：類別命名為 StrategyEntityConfiguration 以避免與 Domain 的
/// <see cref="CryptoBot.Domain.Aggregates.StrategyAggregate.StrategyConfiguration"/> 撞名。
/// </summary>
public sealed class StrategyEntityConfiguration : IEntityTypeConfiguration<DomainStrategy>
{
    public void Configure(EntityTypeBuilder<DomainStrategy> b)
    {
        b.ToTable("Strategies");
        b.HasKey(s => s.Id);

        b.Property(s => s.Name).HasMaxLength(128).IsRequired();
        b.Property(s => s.StrategyType).HasMaxLength(64).IsRequired();
        b.Property(s => s.Status).HasConversion<int>().IsRequired();
        b.Property(s => s.LastError).HasMaxLength(2048);

        b.Property(s => s.CreatedAt).IsRequired();
        b.Property(s => s.TotalTrades).IsRequired();
        b.Property(s => s.WinningTrades).IsRequired();
        b.Property(s => s.LosingTrades).IsRequired();
        b.Property(s => s.CumulativePnL).IsRequired();
        b.Property(s => s.MaxDrawdown).IsRequired();
        b.Property(s => s.PeakPnL).IsRequired();

        // 複合 VO → JSON column
        b.Property(s => s.Configuration)
            .HasConversion(new StrategyConfigurationConverter())
            .HasColumnType("TEXT")
            .IsRequired();

        b.Ignore(s => s.WinRate);
        b.Ignore(s => s.DomainEvents);

        b.HasIndex(s => s.Status);
        b.HasIndex(s => s.Name).IsUnique();
    }
}
