using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Authentication;

public sealed record AuthenticatedDeveloper(Guid TenantId, string Email, bool EmailVerified, AccessToken Token);

/// <summary>
/// Dashboard login: email + password -> dashboard JWT. Returns the same generic
/// error for unknown email / wrong password / inactive, and always runs a password
/// verification (against a dummy hash for unknown accounts) to avoid enumeration.
/// </summary>
public sealed class DeveloperAuthService(
    IApplicationDbContext db,
    IPasswordHasher passwords,
    IJwtTokenService jwt)
{
    public async Task<AuthenticatedDeveloper> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? string.Empty : Tenant.NormalizeEmail(email);

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Email == normalizedEmail, ct);

        var passwordOk = passwords.Verify(password ?? string.Empty, tenant?.PasswordHash);

        if (tenant is null || !tenant.HasPassword || !passwordOk || !tenant.IsActive)
            throw new AuthenticationException("Invalid email or password.");

        return new AuthenticatedDeveloper(tenant.Id, tenant.Email, tenant.EmailVerified, jwt.IssueDashboardToken(tenant));
    }
}
