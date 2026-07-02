using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Tenancy;

public sealed record RegisteredDeveloper(Guid TenantId, string Email, bool EmailVerified, AccessToken DashboardToken);

/// <summary>
/// Registers a developer account (email + password) and returns a dashboard token.
/// Passwords are bcrypt-hashed; emails are normalized and unique.
/// </summary>
public sealed class DeveloperRegistrationService(
    IApplicationDbContext db,
    IPasswordHasher passwords,
    IJwtTokenService jwt)
{
    public async Task<RegisteredDeveloper> RegisterAsync(string name, string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Name is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw new ValidationException("Email is required.");
        if (password is null || password.Length < 12)
            throw new ValidationException("Password must be at least 12 characters.");

        var normalizedEmail = Tenant.NormalizeEmail(email);
        if (await db.Tenants.AnyAsync(t => t.Email == normalizedEmail, ct))
            throw new ConflictException("An account with this email already exists.");

        var tenant = new Tenant(name.Trim(), normalizedEmail, passwords.Hash(password));
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        return new RegisteredDeveloper(tenant.Id, tenant.Email, tenant.EmailVerified, jwt.IssueDashboardToken(tenant));
    }
}
