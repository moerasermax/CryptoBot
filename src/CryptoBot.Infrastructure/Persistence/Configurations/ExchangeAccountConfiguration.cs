using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoBot.Infrastructure.Persistence.Configurations;

public sealed class ExchangeAccountConfiguration : IEntityTypeConfiguration<ExchangeAccount>
{
    public void Configure(EntityTypeBuilder<ExchangeAccount> b)
    {
        b.ToTable("ExchangeAccounts");
        b.HasKey(x => x.Id);

        b.Property(x => x.Exchange).HasConversion<int>().IsRequired();
        b.Property(x => x.AccountName).HasMaxLength(64).IsRequired();
        b.Property(x => x.ApiKey).HasMaxLength(512).IsRequired();
        b.Property(x => x.ApiSecret).HasMaxLength(512).IsRequired();
        b.Property(x => x.IsActive).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();

        b.Ignore(x => x.HasCredentials);
        b.Ignore(x => x.DomainEvents);

        // 同交易所 + 同名稱不可重複（避免 UI 上看不出差別的兩筆）
        b.HasIndex(x => new { x.Exchange, x.AccountName }).IsUnique();
        b.HasIndex(x => new { x.Exchange, x.IsActive });
    }
}
