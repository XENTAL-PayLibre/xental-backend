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

    /// <summary>Issue a session for a team member: scoped to the account's tenant, carrying the member's role.</summary>
    public async Task<IssuedSession> IssueForMemberAsync(TeamMember member, CancellationToken ct = default)
    {
        var access = jwt.IssueDashboardToken(member.TenantId, member.Email, emailVerified: true, member.Role.ToString());
        var raw = tokens.Generate("rt", 32);
        var expires = clock.UtcNow.Add(links.RefreshTokenLifetime);

        db.RefreshTokens.Add(new RefreshToken(member.TenantId, tokenHasher.Hash(raw), expires, teamMemberId: member.Id));
        await db.SaveChangesAsync(ct);

        return new IssuedSession(member.TenantId, member.Email, true, access, raw, expires);
    }
}
