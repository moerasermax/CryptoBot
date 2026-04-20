using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Infrastructure.Persistence.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoBot.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("Orders");
        b.HasKey(o => o.Id);

        b.Property(o => o.Symbol)
            .HasConversion(new SymbolConverter())
            .HasMaxLength(32)
            .IsRequired();

        b.Property(o => o.Side).HasConversion<int>().IsRequired();
        b.Property(o => o.Type).HasConversion<int>().IsRequired();
        b.Property(o => o.Status).HasConversion<int>().IsRequired();
        b.Property(o => o.PositionSide).HasConversion<int>().IsRequired();

        b.Property(o => o.Quantity)
            .HasConversion(new QuantityConverter())
            .IsRequired();

        b.Property(o => o.FilledQuantity)
            .HasConversion(new QuantityConverter())
            .IsRequired();

        b.Property(o => o.LimitPrice).HasConversion(new NullablePriceConverter());
        b.Property(o => o.StopPrice).HasConversion(new NullablePriceConverter());
        b.Property(o => o.AverageFillPrice).HasConversion(new NullablePriceConverter());

        b.Property(o => o.Commission).IsRequired();

        b.Property(o => o.ClientOrderId).HasMaxLength(64);
        b.Property(o => o.ExchangeOrderId).HasMaxLength(64);
        b.Property(o => o.RejectReason).HasMaxLength(512);

        b.Property(o => o.CreatedAt).IsRequired();
        b.Property(o => o.UpdatedAt).IsRequired();

        // 計算屬性與聚合事件不入庫
        b.Ignore(o => o.IsActive);
        b.Ignore(o => o.IsFinalized);
        b.Ignore(o => o.RemainingQuantity);
        b.Ignore(o => o.DomainEvents);

        // 索引（查單常用路徑）
        b.HasIndex(o => o.ExchangeOrderId);
        b.HasIndex(o => o.ClientOrderId);
        b.HasIndex(o => o.StrategyId);
        b.HasIndex(o => o.Status);
        b.HasIndex(o => new { o.Symbol, o.Status });
    }
}
