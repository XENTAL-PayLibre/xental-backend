using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Tenancy;

/// <summary>
/// Magic-link email verification. Issues single-use, hashed, expiring tokens and
/// consumes them to mark an account's email verified.
/// </summary>
public sealed class EmailVerificationService(
    IApplicationDbContext db,
    ITokenGenerator tokens,
    ITokenHasher tokenHasher,
    ILinkBuilder links,
    IEmailSender email,
    IClock clock)
{
    /// <summary>Issue and email a fresh verification link. No-op if already verified.</summary>
    public async Task SendAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null || tenant.EmailVerified)
            return;

        var raw = tokens.Generate("evt", 32);
        db.EmailVerificationTokens.Add(
            new EmailVerificationToken(tenantId, tokenHasher.Hash(raw), clock.UtcNow.Add(links.EmailVerificationTtl)));
        await db.SaveChangesAsync(ct);

        await email.SendEmailVerificationAsync(tenant.Email, links.EmailVerificationLink(raw), ct);
    }

    /// <summary>Consume a token and mark the email verified. Returns false if invalid/expired.</summary>
    public async Task<bool> VerifyAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return false;

        var hash = tokenHasher.Hash(rawToken);
        var token = await db.EmailVerificationTokens
            .IgnoreQueryFilters() // verification is anonymous, no tenant context yet
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(clock.UtcNow))
            return false;

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == token.TenantId, ct);
        if (tenant is null)
            return false;

        tenant.MarkEmailVerified();
        token.Consume(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
