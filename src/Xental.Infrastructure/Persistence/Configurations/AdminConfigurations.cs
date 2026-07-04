using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Admin;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class AdminUserConfiguration : IEntityTypeConfiguration<AdminUser>
{
    public void Configure(EntityTypeBuilder<AdminUser> b)
    {
        b.ToTable("admin_users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).IsRequired().HasMaxLength(320);
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.PasswordHash).IsRequired().HasMaxLength(256);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.TotpSecretEncrypted).HasMaxLength(512);
        b.Property(x => x.CreatedAtUtc).IsRequired();
    }
}

public sealed class AdminAuditLogConfiguration : IEntityTypeConfiguration<AdminAuditLog>
{
    public void Configure(EntityTypeBuilder<AdminAuditLog> b)
    {
        b.ToTable("admin_audit_logs");
        b.HasKey(x => x.Id);
        b.Property(x => x.AdminId).IsRequired();
        b.HasIndex(x => x.AdminId);
        b.Property(x => x.Action).IsRequired().HasMaxLength(64);
        b.Property(x => x.TargetTenantId).HasMaxLength(64);
        b.Property(x => x.Detail).HasMaxLength(500);
        b.Property(x => x.AtUtc).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
    }
}
