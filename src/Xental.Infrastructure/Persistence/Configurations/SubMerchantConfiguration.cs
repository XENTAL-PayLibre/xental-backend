using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Merchants;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class SubMerchantConfiguration : IEntityTypeConfiguration<SubMerchant>
{
    public void Configure(EntityTypeBuilder<SubMerchant> b)
    {
        b.ToTable("sub_merchants");
        b.HasKey(x => x.Id);

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.Name).IsRequired().HasMaxLength(200);
        b.Property(x => x.Reference).IsRequired().HasMaxLength(100);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SettlementBankName).HasMaxLength(200);
        b.Property(x => x.SettlementBankCode).HasMaxLength(16);
        b.Property(x => x.SettlementAccountNumber).HasMaxLength(20);
        b.Property(x => x.SettlementAccountName).HasMaxLength(200);
        b.Property(x => x.PlatformFeeBps).HasDefaultValue(0);
        b.Property(x => x.CreatedAtUtc).IsRequired();

        // Reference is unique per tenant.
        b.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
