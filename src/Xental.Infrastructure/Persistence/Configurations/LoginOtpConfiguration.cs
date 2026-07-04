using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class LoginOtpConfiguration : IEntityTypeConfiguration<LoginOtp>
{
    public void Configure(EntityTypeBuilder<LoginOtp> b)
    {
        b.ToTable("login_otps");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.CodeHash).IsRequired().HasMaxLength(128);
        b.Property(x => x.ExpiresAtUtc).IsRequired();
        b.Property(x => x.Consumed).IsRequired();
        b.Property(x => x.Attempts).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.TeamMemberId, x.Consumed });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
