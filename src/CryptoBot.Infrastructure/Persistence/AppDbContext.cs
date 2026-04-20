using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Infrastructure.Backtesting.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence;

/// <summary>
/// CryptoBot 主要資料庫 Context（SQLite + EF Core 8）。
///
/// 設計原則：
/// - DbSet 對 Aggregate Root（Order / Position / Strategy）一一對應
/// - 所有實體配置走 IEntityTypeConfiguration 集中管理
/// - Domain 層零依賴：所有 EF Core 相關都在本層完成
/// - 未來切換 MongoDB 等其他後端時，本檔案不會被 Application 層引用，可整體替換
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<HistoricalKlineRecord> HistoricalKlines => Set<HistoricalKlineRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
