using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Authentication;

/// <summary>Result of the first login step: a code was emailed and must be verified to get a session.</summary>
public sealed record LoginChallenge(string Email, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Dashboard authentication with mandatory email 2FA. Step 1 (<see cref="BeginLoginAsync"/>) verifies
/// the password and emails a one-time code; step 2 (<see cref="VerifyLoginOtpAsync"/>) checks the code
/// and returns a session (access + refresh). Refresh rotates the token (single-use); logout revokes it.
/// Generic errors + dummy-hash verification prevent account enumeration / timing leaks.
/// </summary>
public sealed class DeveloperAuthService(
    IApplicationDbContext db,
    IPasswordHasher passwords,
    ITokenHasher tokenHasher,
    IEmailSender email,
    SessionIssuer sessions,
    IClock clock)
{
    private static readonly TimeSpan OtpTtl = TimeSpan.FromMinutes(10);

    /// <summary>Step 1: verify the password, then email a one-time login code. Never issues a session.</summary>
    public async Task<LoginChallenge> BeginLoginAsync(string emailAddress, string password, CancellationToken ct = default)
    {
        var normalizedEmail = string.IsNullOrWhiteSpace(emailAddress) ? string.Empty : Tenant.NormalizeEmail(emailAddress);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Email == normalizedEmail, ct);
        if (tenant is not null)
        {
            var passwordOk = passwords.Verify(password ?? string.Empty, tenant.PasswordHash);
            if (!tenant.HasPassword || !passwordOk || !tenant.IsActive)
                throw new AuthenticationException("Invalid email or password.");
            if (!tenant.EmailVerified)
                throw new EmailNotVerifiedException("Please verify your email address before signing in.");
            return await IssueOtpAsync(tenant.Id, null, normalizedEmail, ct);
        }

        // Not an account owner — try an accepted team member (they sign in to the same account).
        var member = await db.TeamMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Email == normalizedEmail && m.Status == TeamMemberStatus.Active, ct);
        var memberOk = passwords.Verify(password ?? string.Empty, member?.PasswordHash);
        if (member is null || !member.CanSignIn || !memberOk)
            throw new AuthenticationException("Invalid email or password.");

        return await IssueOtpAsync(member.TenantId, member.Id, normalizedEmail, ct);
    }

    /// <summary>Step 2: verify the emailed code and issue a session.</summary>
    public async Task<IssuedSession> VerifyLoginOtpAsync(string emailAddress, string code, CancellationToken ct = default)
    {
        var normalizedEmail = string.IsNullOrWhiteSpace(emailAddress) ? string.Empty : Tenant.NormalizeEmail(emailAddress);
        var now = clock.UtcNow;

        // Resolve the subject (owner or team member) exactly as BeginLogin did.
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Email == normalizedEmail, ct);
        Guid tenantId;
        Guid? memberId = null;
        TeamMember? member = null;
        if (tenant is not null && tenant.IsActive)
        {
            tenantId = tenant.Id;
        }
        else
        {
            member = await db.TeamMembers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Email == normalizedEmail && m.Status == TeamMemberStatus.Active, ct);
            if (member is null || !member.CanSignIn)
                throw new AuthenticationException("Invalid or expired code.");
            tenantId = member.TenantId;
            memberId = member.Id;
        }

        var otp = await db.LoginOtps.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.TeamMemberId == memberId && !o.Consumed)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (otp is null || !otp.IsRedeemable(now))
            throw new AuthenticationException("Invalid or expired code.");

        var providedHash = tokenHasher.Hash((code ?? string.Empty).Trim());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedHash), Encoding.UTF8.GetBytes(otp.CodeHash)))
        {
            otp.RegisterFailedAttempt();
            await db.SaveChangesAsync(ct);
            throw new AuthenticationException("Invalid or expired code.");
        }

        otp.Consume();
        await db.SaveChangesAsync(ct);

        return memberId is not null
            ? await sessions.IssueForMemberAsync(member!, ct)
            : await sessions.IssueAsync(tenant!, ct);
    }

    private async Task<LoginChallenge> IssueOtpAsync(Guid tenantId, Guid? memberId, string emailAddress, CancellationToken ct)
    {
        var now = clock.UtcNow;

        // Invalidate earlier unconsumed codes for this subject so only the newest is live.
        var priors = await db.LoginOtps.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.TeamMemberId == memberId && !o.Consumed)
            .ToListAsync(ct);
        foreach (var p in priors) p.Consume();

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var otp = new LoginOtp(tenantId, memberId, tokenHasher.Hash(code), now.Add(OtpTtl));
        db.LoginOtps.Add(otp);
        await db.SaveChangesAsync(ct);

        await email.SendLoginOtpAsync(emailAddress, code, ct);
        return new LoginChallenge(emailAddress, otp.ExpiresAtUtc);
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

        token.Consume(clock.UtcNow); // rotation: this token can't be reused

        // Team-member session — re-issue with the member's identity + role.
        if (token.TeamMemberId is Guid memberId)
        {
            var member = await db.TeamMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == memberId, ct);
            if (member is null || !member.CanSignIn)
                throw new AuthenticationException("Invalid session.");
            return await sessions.IssueForMemberAsync(member, ct);
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == token.TenantId, ct);
        if (tenant is null || !tenant.IsActive)
            throw new AuthenticationException("Invalid session.");

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
