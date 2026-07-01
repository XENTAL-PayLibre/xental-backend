using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Application.Tenancy;

public sealed record DeveloperProfile(
    Guid TenantId,
    string Name,
    string Email,
    bool EmailVerified,
    string Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>Reads the current developer account's own profile.</summary>
public sealed class DeveloperProfileService(IApplicationDbContext db)
{
    public async Task<DeveloperProfile> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Account not found.");

        return new DeveloperProfile(
            tenant.Id, tenant.Name, tenant.Email, tenant.EmailVerified,
            tenant.Status.ToString(), tenant.CreatedAtUtc);
    }
}
