using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Authentication;

/// <summary>A logged-in dashboard session: a short-lived access token + a rotating refresh token.</summary>
public sealed record IssuedSession(
    Guid TenantId,
    string Email,
    bool EmailVerified,
    AccessToken Access,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAtUtc);

/// <summary>Issues a dashboard session (access JWT + persisted, hashed refresh token).</summary>
public sealed class SessionIssuer(
    IApplicationDbContext db,
    IJwtTokenService jwt,
    ITokenGenerator tokens,
    ITokenHasher tokenHasher,
    ILinkBuilder links,
    IClock clock)
{
    public async Task<IssuedSession> IssueAsync(Tenant tenant, CancellationToken ct = default)
    {
        var access = jwt.IssueDashboardToken(tenant);
        var raw = tokens.Generate("rt", 32);
        var expires = clock.UtcNow.Add(links.RefreshTokenLifetime);

        db.RefreshTokens.Add(new RefreshToken(tenant.Id, tokenHasher.Hash(raw), expires));
        await db.SaveChangesAsync(ct);

        return new IssuedSession(tenant.Id, tenant.Email, tenant.EmailVerified, access, raw, expires);
    }
}
