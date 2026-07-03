using Microsoft.EntityFrameworkCore;
using Xental.Domain.Admin;
using Xental.Domain.Merchants;
using Xental.Domain.Onboarding;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Domain.Webhooks;

namespace Xental.Application.Common.Interfaces;

/// <summary>Persistence abstraction the application layer depends on.</summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<ApiKey> ApiKeys { get; }
    DbSet<EmailVerificationToken> EmailVerificationTokens { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<ExternalLogin> ExternalLogins { get; }
    DbSet<SubMerchant> SubMerchants { get; }
    DbSet<Customer> Customers { get; }
    DbSet<VirtualAccount> VirtualAccounts { get; }
    DbSet<Transaction> Transactions { get; }
    DbSet<Transfer> Transfers { get; }
    DbSet<SettlementConfig> SettlementConfigs { get; }
    DbSet<SettlementSplit> SettlementSplits { get; }
    DbSet<EscrowHold> EscrowHolds { get; }
    DbSet<CheckoutSession> CheckoutSessions { get; }
    DbSet<WebhookEndpoint> WebhookEndpoints { get; }
    DbSet<WebhookDelivery> WebhookDeliveries { get; }
    DbSet<OnboardingApplication> OnboardingApplications { get; }
    DbSet<DeveloperKyc> DeveloperKycs { get; }
    DbSet<BusinessKyb> BusinessKybs { get; }
    DbSet<KycDocument> KycDocuments { get; }
    DbSet<VerificationCheck> VerificationChecks { get; }
    DbSet<AdminUser> AdminUsers { get; }
    DbSet<AdminAuditLog> AdminAuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
