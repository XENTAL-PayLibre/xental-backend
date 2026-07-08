using Microsoft.EntityFrameworkCore;
using Xental.Api.Authorization;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Onboarding;

namespace Xental.Api.Auth;

/// <summary>
/// Resolves test/live for the current request from either plane:
/// <list type="bullet">
/// <item>API token → the <c>key_mode</c> claim (live keys are already KYC-gated at creation).</item>
/// <item>Dashboard token → always live. The dashboard is the business plane; test customers and
/// virtual accounts are created via a test API key. Live requires an approved onboarding tier.</item>
/// </list>
/// </summary>
public sealed class ModeContext(
    IHttpContextAccessor accessor,
    IApplicationDbContext db,
    ITenantContext tenant) : IModeContext
{
    public const string ModeHeader = "X-Xental-Mode";

    public async Task<bool> IsLiveAsync(CancellationToken ct = default)
    {
        var user = accessor.HttpContext?.User;
        var scope = user?.FindFirst(AuthPolicies.ScopeClaim)?.Value;

        // API plane: mode comes from the key. A live key already implies approved onboarding.
        if (scope == AuthPolicies.Api)
            return string.Equals(user?.FindFirst("key_mode")?.Value, "live", StringComparison.OrdinalIgnoreCase);

        // Dashboard plane: always live (the business plane). Test customers/virtual accounts are an
        // API-key concern. Live requires an approved live onboarding — same gate as a live API key.
        var tenantId = tenant.RequireTenantId();
        var tier = await db.OnboardingApplications.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => (KycTier?)a.Tier)
            .FirstOrDefaultAsync(ct);
        if (tier != KycTier.Live)
            throw new OnboardingNotApprovedException(
                "Creating live customers requires an approved KYC + KYB onboarding. Complete verification to go live. Test customers and virtual accounts are created with a test API key.");
        return true;
    }
}
