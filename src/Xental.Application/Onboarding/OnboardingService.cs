using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Onboarding;

namespace Xental.Application.Onboarding;

/// <summary>
/// Reads/creates the current tenant's onboarding application. Signup implicitly grants the
/// Sandbox tier — a tenant with no application row is Sandbox by default; the row is created
/// the first time onboarding is touched.
/// </summary>
public sealed class OnboardingService(IApplicationDbContext db, ITenantContext tenantContext)
{
    public async Task<OnboardingApplication> GetOrCreateAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var app = await db.OnboardingApplications.FirstOrDefaultAsync(a => a.TenantId == tenantId, ct);
        if (app is null)
        {
            app = new OnboardingApplication(tenantId);
            db.OnboardingApplications.Add(app);
            await db.SaveChangesAsync(ct);
        }
        return app;
    }

    /// <summary>Current tier for the tenant (Sandbox when no application exists yet).</summary>
    public async Task<KycTier> GetTierAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var tier = await db.OnboardingApplications.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => (KycTier?)a.Tier)
            .FirstOrDefaultAsync(ct);
        return tier ?? KycTier.Sandbox;
    }
}
