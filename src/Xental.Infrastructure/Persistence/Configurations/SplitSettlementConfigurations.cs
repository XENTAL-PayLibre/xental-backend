using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class SettlementSplitConfiguration : IEntityTypeConfiguration<SettlementSplit>
{
    public void Configure(EntityTypeBuilder<SettlementSplit> b)
    {
        b.ToTable("settlement_splits");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.BeneficiaryName).IsRequired().HasMaxLength(200);
        b.Property(x => x.BeneficiaryAccountNumber).IsRequired().HasMaxLength(20);
        b.Property(x => x.BeneficiaryBankCode).IsRequired().HasMaxLength(16);
        b.Property(x => x.Basis).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(x => x.ShareBps).IsRequired();
        b.Property(x => x.FlatKobo).IsRequired();
        b.Property(x => x.Priority).IsRequired();
        b.Property(x => x.Enabled).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.VirtualAccountId });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EscrowHoldConfiguration : IEntityTypeConfiguration<EscrowHold>
{
    public void Configure(EntityTypeBuilder<EscrowHold> b)
    {
        b.ToTable("escrow_holds");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.VirtualAccountId).IsRequired();
        b.Property(x => x.AmountKobo).IsRequired();
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(x => x.ReleaseCondition).HasMaxLength(500);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.VirtualAccountId, x.State });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<VirtualAccount>().WithMany().HasForeignKey(x => x.VirtualAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
