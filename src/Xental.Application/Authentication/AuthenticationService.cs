using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Application.Authentication;

/// <summary>
/// API auth (OAuth2 client-credentials): exchanges an API key's client id + secret
/// for a short-lived API JWT that carries the key's mode (test/live). Generic errors
/// and a dummy-hash verification prevent client-id enumeration / timing leaks.
/// </summary>
public sealed class AuthenticationService(
    IApplicationDbContext db,
    ISecretHasher secrets,
    IJwtTokenService jwt,
    IClock clock)
{
    private const string DummyHash =
        "100000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public async Task<AccessToken> IssueTokenAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new AuthenticationException("Invalid client credentials.");

        // Auth runs with no tenant context yet, so bypass the tenant query filter.
        var key = await db.ApiKeys
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.ClientId == clientId, ct);

        var secretOk = secrets.Verify(clientSecret, key?.SecretHash ?? DummyHash);
        if (key is null || !secretOk || !key.IsActive)
            throw new AuthenticationException("Invalid client credentials.");

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == key.TenantId, ct);
        if (tenant is null || !tenant.IsActive)
            throw new AuthenticationException("Invalid client credentials.");

        key.MarkUsed(clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return jwt.IssueApiToken(tenant, key);
    }
}
