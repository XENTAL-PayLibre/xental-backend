using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Tenancy;

/// <summary>
/// Forgot/reset password via a single-use, hashed, expiring magic link. Requesting a
/// reset never reveals whether an account exists (no enumeration); resetting consumes
/// the token and invalidates every other outstanding reset token for that account.
/// </summary>
public sealed class PasswordResetService(
    IApplicationDbContext db,
    ITokenGenerator tokens,
    ITokenHasher tokenHasher,
    ILinkBuilder links,
    IEmailSender email,
    IPasswordHasher passwords,
    IClock clock)
{
    /// <summary>Email a reset link if the account exists. Always completes silently.</summary>
    public async Task RequestAsync(string emailInput, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(emailInput))
            return;

        var normalized = Tenant.NormalizeEmail(emailInput);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Email == normalized, ct);
        // Only password accounts can reset a password; social-only accounts have none.
        if (tenant is null || !tenant.IsActive || !tenant.HasPassword)
            return;

        var raw = tokens.Generate("prt", 32);
        db.PasswordResetTokens.Add(
            new PasswordResetToken(tenant.Id, tokenHasher.Hash(raw), clock.UtcNow.Add(links.PasswordResetTtl)));
        await db.SaveChangesAsync(ct);

        await email.SendPasswordResetAsync(tenant.Email, links.PasswordResetLink(raw), ct);
    }

    /// <summary>Set a new password from a valid token. Returns false if invalid/expired.</summary>
    public async Task<bool> ResetAsync(string rawToken, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return false;
        Common.PasswordPolicy.Validate(newPassword);

        var hash = tokenHasher.Hash(rawToken);
        var token = await db.PasswordResetTokens
            .IgnoreQueryFilters() // reset is anonymous, no tenant context
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(clock.UtcNow))
            return false;

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == token.TenantId, ct);
        if (tenant is null)
            return false;

        tenant.SetPassword(passwords.Hash(newPassword));

        // Invalidate this and any other outstanding reset tokens for the account.
        var outstanding = await db.PasswordResetTokens
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenant.Id && t.ConsumedAtUtc == null)
            .ToListAsync(ct);
        foreach (var t in outstanding)
            t.Consume(clock.UtcNow);

        await db.SaveChangesAsync(ct);
        return true;
    }
}
