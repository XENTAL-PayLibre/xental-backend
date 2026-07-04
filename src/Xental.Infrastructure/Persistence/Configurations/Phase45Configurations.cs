using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Domain.Webhooks;

namespace Xental.Infrastructure.Persistence.Configurations;

public sealed class TransferConfiguration : IEntityTypeConfiguration<Transfer>
{
    public void Configure(EntityTypeBuilder<Transfer> b)
    {
        b.ToTable("transfers");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.MerchantTxRef).IsRequired().HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.MerchantTxRef }).IsUnique(); // idempotency
        b.Property(x => x.RecipientAccountNumber).IsRequired().HasMaxLength(20);
        b.Property(x => x.RecipientBankCode).IsRequired().HasMaxLength(16);
        b.Property(x => x.RecipientName).HasMaxLength(200);
        b.Property(x => x.Narration).HasMaxLength(200);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ProviderReference).HasMaxLength(128);
        b.Property(x => x.RetryCount).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.ToTable("webhook_endpoints");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.Url).IsRequired().HasMaxLength(2048);
        b.Property(x => x.SecretEncrypted).IsRequired().HasMaxLength(512);
        b.Property(x => x.Active).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => x.TenantId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("webhook_deliveries");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.EndpointId).IsRequired();
        b.Property(x => x.EventId).IsRequired().HasMaxLength(64);
        b.Property(x => x.EventType).IsRequired().HasMaxLength(64);
        b.Property(x => x.PayloadJson).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(512);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc }); // due-delivery scan
        b.HasIndex(x => x.EndpointId);
        b.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
    }
}
