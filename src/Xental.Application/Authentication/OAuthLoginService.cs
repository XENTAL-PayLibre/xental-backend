using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Authentication;

/// <summary>
/// Social login (Google, GitHub). Builds the provider authorize URL and, on callback,
/// exchanges the code for a verified profile then finds-or-creates the matching account
/// and issues a dashboard token. A social login proves control of the email, so the
/// account is marked verified.
/// </summary>
public sealed class OAuthLoginService(
    IApplicationDbContext db,
    IJwtTokenService jwt,
    IEnumerable<IExternalIdentityProvider> providers)
{
    public IExternalIdentityProvider Provider(string name)
    {
        var key = (name ?? string.Empty).Trim().ToLowerInvariant();
        return providers.FirstOrDefault(p => p.Name == key)
            ?? throw new NotFoundException($"Unsupported login provider '{name}'.");
    }

    public string AuthorizationUrl(string providerName, string redirectUri, string state) =>
        Provider(providerName).BuildAuthorizationUrl(redirectUri, state);

    public async Task<AuthenticatedDeveloper> CompleteAsync(string providerName, string code, string redirectUri, CancellationToken ct = default)
    {
        var provider = Provider(providerName);
        if (string.IsNullOrWhiteSpace(code))
            throw new AuthenticationException("Missing authorization code.");

        var profile = await provider.ExchangeCodeAsync(code, redirectUri, ct);
        if (string.IsNullOrWhiteSpace(profile.Email))
            throw new AuthenticationException("The provider did not return an email address.");

        var email = Tenant.NormalizeEmail(profile.Email);

        // Already linked? Log straight in.
        var link = await db.ExternalLogins
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.Provider == provider.Name && l.ProviderUserId == profile.ProviderUserId, ct);

        Tenant tenant;
        if (link is not null)
        {
            tenant = await db.Tenants.FirstAsync(t => t.Id == link.TenantId, ct);
        }
        else
        {
            // Link to an existing account with the same email, or create a new one.
            tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Email == email, ct)
                     ?? CreateTenant(profile, email);
            db.ExternalLogins.Add(new ExternalLogin(tenant.Id, provider.Name, profile.ProviderUserId));
        }

        if (!tenant.IsActive)
            throw new AuthenticationException("This account is suspended.");

        tenant.MarkEmailVerified();
        await db.SaveChangesAsync(ct);

        return new AuthenticatedDeveloper(tenant.Id, tenant.Email, tenant.EmailVerified, jwt.IssueDashboardToken(tenant));
    }

    private Tenant CreateTenant(ExternalUserProfile profile, string email)
    {
        var tenant = new Tenant(string.IsNullOrWhiteSpace(profile.Name) ? email : profile.Name, email, passwordHash: null);
        db.Tenants.Add(tenant);
        return tenant;
    }
}
