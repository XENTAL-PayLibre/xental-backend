using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class EmailVerificationTokenConfiguration : IEntityTypeConfiguration<EmailVerificationToken>
{
    public void Configure(EntityTypeBuilder<EmailVerificationToken> b)
    {
        b.ToTable("email_verification_tokens");
        b.HasKey(x => x.Id);

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.TokenHash).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.ExpiresAtUtc).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();

        b.HasIndex(x => x.TenantId);
        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
