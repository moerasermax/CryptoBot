using CryptoBot.Domain.Aggregates.AiCredentialAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoBot.Infrastructure.Persistence.Configurations;

public sealed class AiCredentialConfiguration : IEntityTypeConfiguration<AiCredential>
{
    public void Configure(EntityTypeBuilder<AiCredential> b)
    {
        b.ToTable("AiCredentials");
        b.HasKey(x => x.Id);

        b.Property(x => x.Provider).HasMaxLength(32).IsRequired();
        b.Property(x => x.ApiKey).HasMaxLength(512).IsRequired();
        // S30-FIX2：Eco/Pro 模式；enum 存 int，既存 row 由 migration 補 0 = Eco。
        b.Property(x => x.Mode).HasConversion<int>().IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();

        b.Ignore(x => x.HasKey);
        b.Ignore(x => x.DomainEvents);

        // 同 provider 最多一筆 — Repository 以此為 Upsert 的邏輯鍵。
        b.HasIndex(x => x.Provider).IsUnique();
    }
}
