using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;

namespace Xental.Application.Admin;

public sealed record AdminLoginResult(AccessToken Token, string Email, string Role);

/// <summary>
/// Admin-plane login: email + password + (when enrolled) a mandatory TOTP second factor. Returns an
/// admin JWT carrying the role. Password verification runs even for unknown accounts (constant-time)
/// so admins can't be enumerated by timing.
/// </summary>
public sealed class AdminAuthService(
    IApplicationDbContext db,
    IPasswordHasher passwords,
    ITotpService totp,
    ISecretProtector protector,
    IJwtTokenService jwt,
    IClock clock)
{
    public async Task<AdminLoginResult> LoginAsync(string email, string password, string? totpCode, CancellationToken ct = default)
    {
        var normalized = AdminUser.NormalizeEmail(email);
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Email == normalized, ct);

        var passwordOk = passwords.Verify(password, admin?.PasswordHash); // dummy-verifies when admin is null
        if (admin is null || !passwordOk || !admin.IsActive)
            throw new AuthenticationException("Invalid credentials.");

        if (admin.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(totpCode) || admin.TotpSecretEncrypted is null
                || !totp.Verify(protector.Unprotect(admin.TotpSecretEncrypted), totpCode))
                throw new AuthenticationException("A valid MFA code is required.");
        }

        admin.MarkLogin(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return new AdminLoginResult(jwt.IssueAdminToken(admin), admin.Email, admin.Role.ToString());
    }
}
