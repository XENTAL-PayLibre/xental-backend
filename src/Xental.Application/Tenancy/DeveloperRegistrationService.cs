using Microsoft.EntityFrameworkCore;
using Xental.Application.Common;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Tenancy;

public sealed record RegisteredDeveloper(Guid TenantId, string Email, bool EmailVerified);

/// <summary>
/// Registers a developer account (email + password). Registration does NOT sign the
/// user in — the account starts unverified and must confirm its email (magic link)
/// before it can log in. Passwords are bcrypt-hashed and must meet the strong-password
/// policy; emails are normalized and unique.
/// </summary>
public sealed class DeveloperRegistrationService(
    IApplicationDbContext db,
    IPasswordHasher passwords)
{
    public async Task<RegisteredDeveloper> RegisterAsync(string name, string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Name is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw new ValidationException("Email is required.");
        PasswordPolicy.Validate(password);

        var normalizedEmail = Tenant.NormalizeEmail(email);
        if (await db.Tenants.AnyAsync(t => t.Email == normalizedEmail, ct))
            throw new ConflictException("An account with this email already exists.");

        var tenant = new Tenant(name.Trim(), normalizedEmail, passwords.Hash(password));
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        return new RegisteredDeveloper(tenant.Id, tenant.Email, tenant.EmailVerified);
    }
}
