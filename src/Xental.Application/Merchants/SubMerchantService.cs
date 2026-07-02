using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Merchants;

namespace Xental.Application.Merchants;

/// <summary>
/// Creates and lists sub-merchants for the current tenant. A sub-merchant is a
/// purely internal Xental record (no Nomba object) — collection happens through the
/// operator's platform Nomba account, and Xental attributes funds via its own ledger.
/// Virtual-account (NUBAN) provisioning against Nomba is Phase 2.
/// </summary>
public sealed class SubMerchantService(
    IApplicationDbContext db,
    ITenantContext tenantContext)
{
    public async Task<SubMerchant> CreateAsync(string name, string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Sub-merchant name is required.");
        if (string.IsNullOrWhiteSpace(reference))
            throw new ValidationException("Sub-merchant reference is required.");

        var tenantId = tenantContext.RequireTenantId();
        reference = reference.Trim();

        // Query filter scopes this to the current tenant, so uniqueness is per-tenant.
        if (await db.SubMerchants.AnyAsync(s => s.Reference == reference, ct))
            throw new ConflictException($"A sub-merchant with reference '{reference}' already exists.");

        var sub = new SubMerchant(tenantId, name.Trim(), reference);
        db.SubMerchants.Add(sub);
        await db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<IReadOnlyList<SubMerchant>> ListAsync(CancellationToken ct = default) =>
        await db.SubMerchants
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(ct);
}
