using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Merchants;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("customers");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.Reference).IsRequired().HasMaxLength(100);
        b.Property(x => x.Name).IsRequired().HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Phone).HasMaxLength(32);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class VirtualAccountConfiguration : IEntityTypeConfiguration<VirtualAccount>
{
    public void Configure(EntityTypeBuilder<VirtualAccount> b)
    {
        b.ToTable("virtual_accounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.CustomerId).IsRequired();
        b.Property(x => x.Reference).IsRequired().HasMaxLength(100);
        b.Property(x => x.AccountNumber).IsRequired().HasMaxLength(20);
        b.Property(x => x.BankName).IsRequired().HasMaxLength(120);
        b.Property(x => x.AccountName).IsRequired().HasMaxLength(200);
        b.Property(x => x.ProviderAccountId).HasMaxLength(100);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.PaymentState).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.SettledUpToKobo).HasDefaultValue(0L);
        b.Property(x => x.CreatedAtUtc).IsRequired();

        b.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
        b.HasIndex(x => x.AccountNumber).IsUnique(); // NUBAN maps to exactly one virtual account
        b.HasIndex(x => x.SubMerchantId); // settlement routing + per-sub-merchant balance queries
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SubMerchant>().WithMany().HasForeignKey(x => x.SubMerchantId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> b)
    {
        b.ToTable("transactions");
        b.HasKey(x => x.Id);
        b.Property(x => x.NombaReference).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.NombaReference).IsUnique(); // idempotency: same reference => duplicate
        b.Property(x => x.TransferName).HasMaxLength(200);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Reconciliation).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.OccurredAtUtc).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();

        b.HasIndex(x => x.TenantId);
        b.HasIndex(x => x.VirtualAccountId);
        b.HasIndex(x => x.Reconciliation); // review-queue queries (PendingReview)
    }
}
