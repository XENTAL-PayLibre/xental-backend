using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class MoneyRuleConfiguration : IEntityTypeConfiguration<MoneyRule>
{
    public void Configure(EntityTypeBuilder<MoneyRule> b)
    {
        b.ToTable("money_rules");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.ThresholdKobo);
        b.Property(x => x.MinRiskScore);
        b.Property(x => x.Enabled).IsRequired();
        b.Property(x => x.Priority).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Enabled });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
