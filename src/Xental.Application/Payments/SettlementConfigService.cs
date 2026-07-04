using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>Reads/updates the current tenant's settlement preferences (one row per tenant).</summary>
public sealed class SettlementConfigService(IApplicationDbContext db, ITenantContext tenantContext)
{
    public async Task<SettlementConfig> GetAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.SettlementConfigs.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct)
            ?? new SettlementConfig(tenantId); // defaults (auto-settle off) until configured
    }

    public async Task<SettlementConfig> UpdateAsync(
        string? accountNumber, string? bankCode, string? accountName, bool autoSettle, long minPayoutKobo, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var config = await db.SettlementConfigs.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (config is null)
        {
            config = new SettlementConfig(tenantId);
            db.SettlementConfigs.Add(config);
        }
        config.Update(accountNumber, bankCode, accountName, autoSettle, minPayoutKobo);
        await db.SaveChangesAsync(ct);
        return config;
    }
}
