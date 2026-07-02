using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class SettlementConfigConfiguration : IEntityTypeConfiguration<SettlementConfig>
{
    public void Configure(EntityTypeBuilder<SettlementConfig> b)
    {
        b.ToTable("settlement_configs");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.HasIndex(x => x.TenantId).IsUnique(); // one settlement profile per tenant
        b.Property(x => x.SettlementAccountNumber).HasMaxLength(20);
        b.Property(x => x.SettlementBankCode).HasMaxLength(16);
        b.Property(x => x.SettlementAccountName).HasMaxLength(200);
        b.Property(x => x.AutoSettle).IsRequired();
        b.Property(x => x.MinPayoutKobo).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
