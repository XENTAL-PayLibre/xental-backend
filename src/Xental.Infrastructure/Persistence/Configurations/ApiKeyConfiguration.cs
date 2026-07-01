using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("api_keys");
        b.HasKey(x => x.Id);

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.ClientId).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.ClientId).IsUnique();
        b.Property(x => x.SecretHash).IsRequired().HasMaxLength(256);
        b.Property(x => x.Label).IsRequired().HasMaxLength(100);
        b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.CreatedAtUtc).IsRequired();

        b.HasIndex(x => x.TenantId);
        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
