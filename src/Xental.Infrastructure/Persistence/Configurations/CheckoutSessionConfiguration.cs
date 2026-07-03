using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class CheckoutSessionConfiguration : IEntityTypeConfiguration<CheckoutSession>
{
    public void Configure(EntityTypeBuilder<CheckoutSession> b)
    {
        b.ToTable("checkout_sessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.VirtualAccountId).IsRequired();
        b.Property(x => x.Token).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.Token).IsUnique();
        b.Property(x => x.ExpiresAtUtc).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<VirtualAccount>().WithMany().HasForeignKey(x => x.VirtualAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
