using CryptoBot.Domain.Aggregates.StrategyOptimizationAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoBot.Infrastructure.Persistence.Configurations;

/// <summary>
/// S22 — 策略最佳化設定快照表。
///
/// 主鍵設計：複合 (StrategyId, Symbol, Interval)，語意即「同策略同交易對同週期只有一組最佳參數」。
/// 沿用 <see cref="HistoricalKlineConfiguration"/> 的複合鍵模式，避免額外的 Guid Id 膨脹。
///
/// 前綴索引 (StrategyId) 天然由複合主鍵的第一欄涵蓋，Lab 頁「列出某策略所有週期 / Symbol 的最佳化結果」
/// 會走主鍵掃描。
/// </summary>
public sealed class StrategyOptimizationSettingsConfiguration
    : IEntityTypeConfiguration<StrategyOptimizationSettings>
{
    public void Configure(EntityTypeBuilder<StrategyOptimizationSettings> b)
    {
        b.ToTable("StrategyOptimizationSettings");

        b.HasKey(x => new { x.StrategyId, x.Symbol, x.Interval });

        b.Property(x => x.StrategyId).IsRequired();
        b.Property(x => x.Symbol).HasMaxLength(32).IsRequired();
        b.Property(x => x.Interval).HasConversion<int>().IsRequired();

        // ParametersJson 由策略側自定 schema，長度放寬到 8K 覆蓋多參數網格結果。
        b.Property(x => x.ParametersJson).HasMaxLength(8192).IsRequired();
        b.Property(x => x.Score).HasPrecision(28, 12).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();
    }
}
