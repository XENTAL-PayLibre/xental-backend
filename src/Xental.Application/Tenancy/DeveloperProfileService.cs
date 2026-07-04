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
public sealed class DeveloperProfileService(IApplicationDbContext db)
{
    public async Task<DeveloperProfile> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Account not found.");

        return new DeveloperProfile(
            tenant.Id, tenant.Name, tenant.Email, tenant.BrandName, tenant.EmailVerified,
            tenant.Status.ToString(), tenant.CreatedAtUtc);
    }

    /// <summary>Set the public brand/product name shown to payers.</summary>
    public async Task<DeveloperProfile> SetBrandNameAsync(Guid tenantId, string? brandName, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Account not found.");
        tenant.SetBrandName(brandName);
        await db.SaveChangesAsync(ct);
        return await GetAsync(tenantId, ct);
    }
}
