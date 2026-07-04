using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Billing;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class BillingScheduleConfiguration : IEntityTypeConfiguration<BillingSchedule>
{
    public void Configure(EntityTypeBuilder<BillingSchedule> b)
    {
        b.ToTable("billing_schedules");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.VirtualAccountId).IsRequired();
        b.Property(x => x.CustomerId).IsRequired();
        b.Property(x => x.Reference).IsRequired().HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
        b.HasIndex(x => x.VirtualAccountId);
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.Interval).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(x => x.NextAmountKobo).IsRequired();
        b.Property(x => x.DueOffsetDays).IsRequired();
        b.Property(x => x.PeriodsGenerated).IsRequired();
        b.Property(x => x.CarryCreditKobo).IsRequired();
        b.Property(x => x.AttributedUpToKobo).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BillingPeriodConfiguration : IEntityTypeConfiguration<BillingPeriod>
{
    public void Configure(EntityTypeBuilder<BillingPeriod> b)
    {
        b.ToTable("billing_periods");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.BillingScheduleId).IsRequired();
        b.HasIndex(x => new { x.BillingScheduleId, x.Sequence }).IsUnique();
        b.HasIndex(x => x.Status);
        b.Property(x => x.Sequence).IsRequired();
        b.Property(x => x.ExpectedAmountKobo).IsRequired();
        b.Property(x => x.AmountAttributedKobo).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<BillingSchedule>().WithMany().HasForeignKey(x => x.BillingScheduleId).OnDelete(DeleteBehavior.Cascade);
    }
}
