using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> b)
    {
        b.ToTable("external_logins");
        b.HasKey(x => x.Id);

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.Provider).IsRequired().HasMaxLength(32);
        b.Property(x => x.ProviderUserId).IsRequired().HasMaxLength(256);
        b.Property(x => x.CreatedAtUtc).IsRequired();

        // A given provider identity maps to exactly one account.
        b.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();
        b.HasIndex(x => x.TenantId);
        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
