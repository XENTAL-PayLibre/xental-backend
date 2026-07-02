using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Merchants;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Persistence;

public sealed class XentalDbContext : DbContext, IApplicationDbContext
{
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
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<SubMerchant> SubMerchants => Set<SubMerchant>();

    // Referenced by the tenant query filter; evaluated per query against the
    // current request's tenant. Guid.Empty (no tenant) matches no rows -> deny by default.
    private Guid CurrentTenantId => _tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(XentalDbContext).Assembly);

        // Row-level tenant isolation for every tenant-owned entity.
        modelBuilder.Entity<ApiKey>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmailVerificationToken>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<PasswordResetToken>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ExternalLogin>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SubMerchant>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

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
        EnforceTenantIsolation();
        return base.SaveChangesAsync(cancellationToken);
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
