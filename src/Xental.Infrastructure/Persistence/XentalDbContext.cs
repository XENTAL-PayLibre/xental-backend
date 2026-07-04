using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Merchants;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Domain.Webhooks;

namespace Xental.Infrastructure.Persistence;

public sealed class XentalDbContext : DbContext, IApplicationDbContext
{
    /// <summary>Shadow property name for the self-managed optimistic-concurrency token.</summary>
    private const string ConcurrencyToken = "ConcurrencyToken";

    private readonly ITenantContext _tenantContext;
    private readonly IClock _clock;

    public XentalDbContext(
        DbContextOptions<XentalDbContext> options,
        ITenantContext tenantContext,
        IClock clock) : base(options)
    {
        _tenantContext = tenantContext;
        _clock = clock;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LoginOtp> LoginOtps => Set<LoginOtp>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<SubMerchant> SubMerchants => Set<SubMerchant>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<VirtualAccount> VirtualAccounts => Set<VirtualAccount>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<SettlementConfig> SettlementConfigs => Set<SettlementConfig>();
    public DbSet<SettlementSplit> SettlementSplits => Set<SettlementSplit>();
    public DbSet<EscrowHold> EscrowHolds => Set<EscrowHold>();
    public DbSet<MoneyRule> MoneyRules => Set<MoneyRule>();
    public DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();
    public DbSet<Xental.Domain.Billing.BillingSchedule> BillingSchedules => Set<Xental.Domain.Billing.BillingSchedule>();
    public DbSet<Xental.Domain.Billing.BillingPeriod> BillingPeriods => Set<Xental.Domain.Billing.BillingPeriod>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<Xental.Domain.Onboarding.OnboardingApplication> OnboardingApplications => Set<Xental.Domain.Onboarding.OnboardingApplication>();
    public DbSet<Xental.Domain.Onboarding.DeveloperKyc> DeveloperKycs => Set<Xental.Domain.Onboarding.DeveloperKyc>();
    public DbSet<Xental.Domain.Onboarding.BusinessKyb> BusinessKybs => Set<Xental.Domain.Onboarding.BusinessKyb>();
    public DbSet<Xental.Domain.Onboarding.KycDocument> KycDocuments => Set<Xental.Domain.Onboarding.KycDocument>();
    public DbSet<Xental.Domain.Onboarding.VerificationCheck> VerificationChecks => Set<Xental.Domain.Onboarding.VerificationCheck>();
    public DbSet<Xental.Domain.Admin.AdminUser> AdminUsers => Set<Xental.Domain.Admin.AdminUser>();
    public DbSet<Xental.Domain.Admin.AdminAuditLog> AdminAuditLogs => Set<Xental.Domain.Admin.AdminAuditLog>();

    // Referenced by the tenant query filter; evaluated per query against the
    // current request's tenant. Guid.Empty (no tenant) matches no rows -> deny by default.
    private Guid CurrentTenantId => _tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(XentalDbContext).Assembly);

        // Row-level tenant isolation for every tenant-owned entity.
        modelBuilder.Entity<TeamMember>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ApiKey>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmailVerificationToken>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<PasswordResetToken>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<LoginOtp>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ExternalLogin>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SubMerchant>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Customer>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<VirtualAccount>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Transfer>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SettlementConfig>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SettlementSplit>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<EscrowHold>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<MoneyRule>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<CheckoutSession>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Xental.Domain.Billing.BillingSchedule>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Xental.Domain.Billing.BillingPeriod>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<WebhookEndpoint>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<WebhookDelivery>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Xental.Domain.Onboarding.OnboardingApplication>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Xental.Domain.Onboarding.DeveloperKyc>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Xental.Domain.Onboarding.BusinessKyb>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Xental.Domain.Onboarding.KycDocument>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Xental.Domain.Onboarding.VerificationCheck>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        // Transactions are written by the webhook processor without a tenant context (and may
        // have no tenant when the account is unknown), so no global filter — reads are filtered
        // explicitly by TenantId in the tenant-scoped services (Phase 5).

        // Optimistic concurrency on the mutable money/billing aggregates. Their running totals
        // (AmountPaidKobo, SettledUpToKobo, AttributedUpToKobo, CarryCreditKobo, counters) are
        // read-modify-written from multiple paths (concurrent webhooks, the billing worker vs the
        // reconciliation path), so a bare UPDATE would lose updates. A self-managed integer token
        // (bumped in SaveChanges for modified rows) makes EF add `AND ConcurrencyToken = @original` to
        // every UPDATE; a lost update then surfaces as DbUpdateConcurrencyException and the callers
        // reload + retry. Provider-agnostic (real column), so it also works under the SQLite tests.
        modelBuilder.Entity<VirtualAccount>().Property<int>(ConcurrencyToken).IsConcurrencyToken();
        modelBuilder.Entity<Xental.Domain.Billing.BillingSchedule>().Property<int>(ConcurrencyToken).IsConcurrencyToken();

        // At most one non-cancelled billing schedule per virtual account (DB-enforced; the app-level
        // AnyAsync check races). Partial unique index — Postgres-only filter syntax.
        if (Database.IsNpgsql())
            modelBuilder.Entity<Xental.Domain.Billing.BillingSchedule>()
                .HasIndex(x => x.VirtualAccountId)
                .IsUnique()
                .HasFilter("\"Status\" <> 'Cancelled'")
                .HasDatabaseName("UX_billing_schedules_active_virtual_account");

        // SQLite (test provider) cannot ORDER BY/compare DateTimeOffset; store it as
        // a sortable binary long there. Postgres keeps native timestamptz.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var converter = new DateTimeOffsetToBinaryConverter();
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties()
                             .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
                {
                    property.SetValueConverter(converter);
                }
            }
        }

        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        BumpConcurrencyTokens();
        EnforceTenantIsolation();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Increment the concurrency token on every modified aggregate that carries one, so EF's
    /// UPDATE checks the original value and a concurrent write is rejected (DbUpdateConcurrencyException).</summary>
    private void BumpConcurrencyTokens()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified) continue;
            var prop = entry.Metadata.FindProperty(ConcurrencyToken);
            if (prop is null) continue;
            var current = entry.Property(ConcurrencyToken).CurrentValue;
            entry.Property(ConcurrencyToken).CurrentValue = (current as int? ?? 0) + 1;
        }
    }

    private void ApplyAuditTimestamps()
    {
        var now = _clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAtUtc = now;
            else if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAtUtc = now;
        }
    }

    private void EnforceTenantIsolation()
    {
        if (_tenantContext.TenantId is not Guid tenantId)
            return; // system/unauthenticated contexts (e.g. registration) are not tenant-scoped

        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            var property = entry.Property(nameof(ITenantOwned.TenantId));
            switch (entry.State)
            {
                case EntityState.Added:
                    if ((Guid)property.CurrentValue! != tenantId)
                        throw new InvalidOperationException("Cross-tenant write blocked.");
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    if ((Guid)property.OriginalValue! != tenantId)
                        throw new InvalidOperationException("Cross-tenant modification blocked.");
                    break;
            }
        }
    }
}
