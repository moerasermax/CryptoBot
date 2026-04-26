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

        // S66-C：訊號鏈路追蹤 ID（StrategyExecutor 在 K 線 tick 開頭生成的 12 字 hash）
        b.Property(o => o.TraceId).HasMaxLength(32);

        b.Property(o => o.CreatedAt).IsRequired();
        b.Property(o => o.UpdatedAt).IsRequired();

        // 計算屬性與聚合事件不入庫
        b.Ignore(o => o.IsActive);
        b.Ignore(o => o.IsFinalized);
        b.Ignore(o => o.RemainingQuantity);
        b.Ignore(o => o.DomainEvents);

        // 索引（查單常用路徑）
        b.HasIndex(o => o.ExchangeOrderId);

        // S66-A：ClientOrderId 唯一索引 — 同一個訊號特徵（決定性 hash）絕不允許產出兩筆訂單。
        // 過濾條件：當 ClientOrderId IS NOT NULL（歷史 row 可能為 null，避免 backfill 造成衝突）。
        // 假設前提：當前單帳戶單交易所部署。未來若導入多帳戶 / 多交易所，需改為複合 unique
        //         (ExchangeAccountId, ClientOrderId) 或 (ExchangeName, ClientOrderId)。
        b.HasIndex(o => o.ClientOrderId)
            .IsUnique()
            .HasFilter("\"ClientOrderId\" IS NOT NULL");

        b.HasIndex(o => o.StrategyId);
        b.HasIndex(o => o.Status);
        b.HasIndex(o => new { o.Symbol, o.Status });
    }
}
