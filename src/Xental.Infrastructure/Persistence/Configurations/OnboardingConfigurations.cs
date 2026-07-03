using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Onboarding;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class OnboardingApplicationConfiguration : IEntityTypeConfiguration<OnboardingApplication>
{
    public void Configure(EntityTypeBuilder<OnboardingApplication> b)
    {
        b.ToTable("onboarding_applications");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.HasIndex(x => x.TenantId).IsUnique(); // one application per tenant
        b.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.DeveloperKycStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.BusinessKybStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DecisionReason).HasMaxLength(1000);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeveloperKycConfiguration : IEntityTypeConfiguration<DeveloperKyc>
{
    public void Configure(EntityTypeBuilder<DeveloperKyc> b)
    {
        b.ToTable("developer_kyc");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.HasIndex(x => x.TenantId).IsUnique(); // one KYC record per tenant
        b.Property(x => x.FullName).IsRequired().HasMaxLength(200);
        b.Property(x => x.Country).IsRequired().HasMaxLength(100);
        b.Property(x => x.Address).IsRequired().HasMaxLength(500);
        b.Property(x => x.IdType).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.IdNumberEncrypted).IsRequired().HasMaxLength(512); // ciphertext, not raw
        b.Property(x => x.IdNumberHash).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.IdNumberHash); // dedup / reuse detection
        b.Property(x => x.BankName).IsRequired().HasMaxLength(200);
        b.Property(x => x.BankCode).IsRequired().HasMaxLength(16);
        b.Property(x => x.BankAccountName).IsRequired().HasMaxLength(200);
        b.Property(x => x.BankAccountNumber).IsRequired().HasMaxLength(20);
        b.Property(x => x.PortfolioUrl).HasMaxLength(500);
        b.Property(x => x.ProjectDescription).HasMaxLength(2000);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BusinessKybConfiguration : IEntityTypeConfiguration<BusinessKyb>
{
    public void Configure(EntityTypeBuilder<BusinessKyb> b)
    {
        b.ToTable("business_kyb");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.HasIndex(x => x.TenantId).IsUnique();
        b.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
        b.Property(x => x.RegistrationNumber).IsRequired().HasMaxLength(50);
        b.Property(x => x.BusinessType).IsRequired().HasMaxLength(100);
        b.Property(x => x.Industry).IsRequired().HasMaxLength(100);
        b.Property(x => x.Country).IsRequired().HasMaxLength(100);
        b.Property(x => x.Address).IsRequired().HasMaxLength(500);
        b.Property(x => x.ContactCountryCode).IsRequired().HasMaxLength(8);
        b.Property(x => x.ContactPhone).IsRequired().HasMaxLength(32);
        b.Property(x => x.Website).HasMaxLength(300);
        b.Property(x => x.SettlementBankName).IsRequired().HasMaxLength(200);
        b.Property(x => x.SettlementBankCode).IsRequired().HasMaxLength(16);
        b.Property(x => x.SettlementAccountName).IsRequired().HasMaxLength(200);
        b.Property(x => x.SettlementAccountNumber).IsRequired().HasMaxLength(20);
        b.Property(x => x.AttestationIp).HasMaxLength(64);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class KycDocumentConfiguration : IEntityTypeConfiguration<KycDocument>
{
    public void Configure(EntityTypeBuilder<KycDocument> b)
    {
        b.ToTable("kyc_documents");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Type });
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.ObjectKey).IsRequired().HasMaxLength(256);
        b.Property(x => x.ContentHash).IsRequired().HasMaxLength(64);
        b.Property(x => x.ContentType).IsRequired().HasMaxLength(64);
        b.Property(x => x.ReviewStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class VerificationCheckConfiguration : IEntityTypeConfiguration<VerificationCheck>
{
    public void Configure(EntityTypeBuilder<VerificationCheck> b)
    {
        b.ToTable("verification_checks");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Kind });
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Provider).IsRequired().HasMaxLength(32);
        b.Property(x => x.Detail).HasMaxLength(300);
        b.Property(x => x.CheckedAtUtc).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
