using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Authentication;

/// <summary>
/// Dashboard authentication. Login requires a verified email and returns a session
/// (access + refresh). Refresh rotates the token (single-use); logout revokes it.
/// Generic errors + dummy-hash verification prevent account enumeration / timing leaks.
/// </summary>
public sealed class DeveloperAuthService(
    IApplicationDbContext db,
    IPasswordHasher passwords,
    ITokenHasher tokenHasher,
    SessionIssuer sessions,
    IClock clock)
{
    public async Task<IssuedSession> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? string.Empty : Tenant.NormalizeEmail(email);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Email == normalizedEmail, ct);
        var passwordOk = passwords.Verify(password ?? string.Empty, tenant?.PasswordHash);

        if (tenant is null || !tenant.HasPassword || !passwordOk || !tenant.IsActive)
            throw new AuthenticationException("Invalid email or password.");

        if (!tenant.EmailVerified)
            throw new EmailNotVerifiedException("Please verify your email address before signing in.");

        return await sessions.IssueAsync(tenant, ct);
    }

    /// <summary>Rotate a refresh token: consume the presented one, issue a fresh session.</summary>
    public async Task<IssuedSession> RefreshAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
            throw new AuthenticationException("Invalid session.");

        var hash = tokenHasher.Hash(rawRefreshToken);
        var token = await db.RefreshTokens
            .IgnoreQueryFilters() // refresh runs without tenant context (access token may be expired)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null || !token.IsActive(clock.UtcNow))
            throw new AuthenticationException("Invalid session.");

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == token.TenantId, ct);
        if (tenant is null || !tenant.IsActive)
            throw new AuthenticationException("Invalid session.");

        token.Consume(clock.UtcNow); // rotation: this token can't be reused
        return await sessions.IssueAsync(tenant, ct);
    }

    /// <summary>Revoke a refresh token (logout). Silent if the token is unknown.</summary>
    public async Task LogoutAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
            return;

        var hash = tokenHasher.Hash(rawRefreshToken);
        var token = await db.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is not null && token.RevokedAtUtc is null)
        {
            token.Revoke(clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }
    }
}
