using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class FlowConfiguration : IEntityTypeConfiguration<Flow>
{
    public void Configure(EntityTypeBuilder<Flow> b)
    {
        b.ToTable("flows");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.MinAmountKobo);
        b.Property(x => x.MinRiskScore);
        b.Property(x => x.Enabled).IsRequired();
        b.Property(x => x.Priority).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Enabled });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Actions)
            .WithOne()
            .HasForeignKey(a => a.FlowId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Metadata.FindNavigation(nameof(Flow.Actions))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class FlowActionConfiguration : IEntityTypeConfiguration<FlowAction>
{
    public void Configure(EntityTypeBuilder<FlowAction> b)
    {
        b.ToTable("flow_actions");
        b.HasKey(x => x.Id);
        b.Property(x => x.FlowId).IsRequired();
        b.Property(x => x.Order).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.HasIndex(x => x.FlowId);
    }
}

public sealed class FlowRunConfiguration : IEntityTypeConfiguration<FlowRun>
{
    public void Configure(EntityTypeBuilder<FlowRun> b)
    {
        b.ToTable("flow_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.FlowId).IsRequired();
        b.Property(x => x.FlowName).HasMaxLength(120).IsRequired();
        b.Property(x => x.Trigger).HasMaxLength(24).IsRequired();
        b.Property(x => x.AccountRef).HasMaxLength(100);
        b.Property(x => x.TransactionRef).HasMaxLength(100);
        b.Property(x => x.Outcome).HasMaxLength(2000).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
