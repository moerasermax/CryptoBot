using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Infrastructure.Persistence.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoBot.Infrastructure.Persistence.Configurations;

public sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> b)
    {
        b.ToTable("Positions");
        b.HasKey(p => p.Id);

        b.Property(p => p.Symbol)
            .HasConversion(new SymbolConverter())
            .HasMaxLength(32)
            .IsRequired();

        b.Property(p => p.Side).HasConversion<int>().IsRequired();
        b.Property(p => p.MarginMode).HasConversion<int>().IsRequired();

        b.Property(p => p.Quantity)
            .HasConversion(new QuantityConverter())
            .IsRequired();

        b.Property(p => p.EntryPrice)
            .HasConversion(new PriceConverter())
            .IsRequired();

        b.Property(p => p.CurrentPrice).HasConversion(new NullablePriceConverter());
        b.Property(p => p.StopLossPrice).HasConversion(new NullablePriceConverter());
        b.Property(p => p.TakeProfitPrice).HasConversion(new NullablePriceConverter());
        b.Property(p => p.TrailingStopPrice).HasConversion(new NullablePriceConverter());
        b.Property(p => p.ExitPrice).HasConversion(new NullablePriceConverter());

        // S39: 策略類型字串快照（不跟 Strategy 外鍵 — 歷史紀錄不怕上游改名）
        b.Property(p => p.StrategyType).HasMaxLength(64);
        // S39: 開倉當下的參數 JSON 快照，給 AI 複盤讀用
        b.Property(p => p.ParametersSnapshot);

        b.Property(p => p.Leverage)
            .HasConversion(new LeverageConverter())
            .IsRequired();

        b.Property(p => p.TrailingStopPercent);
        b.Property(p => p.RealizedPnL).IsRequired();
        b.Property(p => p.TotalCommission).IsRequired();

        b.Property(p => p.OpenedAt).IsRequired();
        b.Property(p => p.IsClosed).IsRequired();

        // 計算屬性 — 全數忽略
        b.Ignore(p => p.UnrealizedPnL);
        b.Ignore(p => p.UnrealizedPnLPercent);
        b.Ignore(p => p.NotionalValue);
        b.Ignore(p => p.InitialMargin);
        b.Ignore(p => p.EstimatedLiquidationPrice);
        b.Ignore(p => p.LiquidationRiskPercent);
        b.Ignore(p => p.DomainEvents);

        b.HasIndex(p => p.StrategyId);
        b.HasIndex(p => p.IsClosed);
        b.HasIndex(p => new { p.Symbol, p.IsClosed });
    }
}
