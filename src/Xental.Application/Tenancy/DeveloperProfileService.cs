using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Application.Tenancy;

public sealed record DeveloperProfile(
    Guid TenantId,
    string Name,
    string Email,
    string? BrandName,
    bool EmailVerified,
    string Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>Reads and updates the current developer account's own profile.</summary>
public sealed class DeveloperProfileService(IApplicationDbContext db, IPasswordHasher passwords)
{
    public async Task<DeveloperProfile> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Account not found.");

        return new DeveloperProfile(
            tenant.Id, tenant.Name, tenant.Email, tenant.BrandName, tenant.EmailVerified,
            tenant.Status.ToString(), tenant.CreatedAtUtc);
    }

    /// <summary>Change the account's password after verifying the current one.</summary>
    public async Task ChangePasswordAsync(Guid tenantId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Account not found.");

        if (!tenant.HasPassword)
            throw new ValidationException("This account signs in with a social provider and has no password to change.");
        if (!passwords.Verify(currentPassword ?? string.Empty, tenant.PasswordHash))
            throw new ValidationException("Your current password is incorrect.");

        Common.PasswordPolicy.Validate(newPassword);
        tenant.SetPassword(passwords.Hash(newPassword));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Set the public brand/product name shown to payers.</summary>
    public async Task<DeveloperProfile> SetBrandNameAsync(Guid tenantId, string? brandName, CancellationToken ct = default)
    {
        // The brand is rendered to payers (checkout pages, transactional emails), so reject markup here
        // for a clean 400 (the domain enforces the same invariant as defense in depth).
        if (brandName is not null && brandName.IndexOfAny(['<', '>']) >= 0)
            throw new ValidationException("Brand name cannot contain '<' or '>'.");

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Account not found.");
        tenant.SetBrandName(brandName);
        await db.SaveChangesAsync(ct);
        return await GetAsync(tenantId, ct);
    }
}
