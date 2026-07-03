using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>A requested split leg (tenant-wide) from the settings API.</summary>
public sealed record SplitSpec(
    string BeneficiaryName,
    string AccountNumber,
    string BankCode,
    string Basis,        // "Percentage" | "Flat"
    int ShareBps,
    long FlatKobo,
    int Priority);

/// <summary>
/// Manages a tenant's split-settlement plan and per-account escrow holds (dashboard plane). The
/// settlement worker reads these when sweeping. Purely configuration — nothing here moves money.
/// </summary>
public sealed class SplitSettlementService(IApplicationDbContext db, ITenantContext tenantContext, IClock clock)
{
    public async Task<IReadOnlyList<SettlementSplit>> GetSplitsAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.SettlementSplits.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.VirtualAccountId == null)
            .OrderBy(s => s.Priority).ToListAsync(ct);
    }

    /// <summary>Replace the tenant-wide split plan atomically (empty list clears it → single sweep).</summary>
    public async Task<IReadOnlyList<SettlementSplit>> SetSplitsAsync(IEnumerable<SplitSpec> specs, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var existing = await db.SettlementSplits
            .Where(s => s.TenantId == tenantId && s.VirtualAccountId == null).ToListAsync(ct);
        db.SettlementSplits.RemoveRange(existing);

        var created = new List<SettlementSplit>();
        foreach (var s in specs)
        {
            var basis = string.Equals(s.Basis, "Flat", StringComparison.OrdinalIgnoreCase) ? SplitBasis.Flat : SplitBasis.Percentage;
            var split = new SettlementSplit(tenantId, null, s.BeneficiaryName, s.AccountNumber, s.BankCode, basis, s.ShareBps, s.FlatKobo, s.Priority);
            db.SettlementSplits.Add(split);
            created.Add(split);
        }
        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>Place (or return the existing) escrow hold on an account so it is not swept.</summary>
    public async Task<EscrowHold> HoldAsync(string accountRef, string? condition, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var account = await db.VirtualAccounts.FirstOrDefaultAsync(v => v.Reference == accountRef, ct)
            ?? throw new NotFoundException($"Virtual account '{accountRef}' not found.");
        var active = await db.EscrowHolds.FirstOrDefaultAsync(e => e.VirtualAccountId == account.Id && e.State == EscrowState.Held, ct);
        if (active is not null)
            return active;
        var hold = new EscrowHold(tenantId, account.Id, account.AmountPaidKobo, condition);
        db.EscrowHolds.Add(hold);
        await db.SaveChangesAsync(ct);
        return hold;
    }

    /// <summary>Release the active escrow hold so the next sweep can settle the account.</summary>
    public async Task ReleaseAsync(string accountRef, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        var account = await db.VirtualAccounts.FirstOrDefaultAsync(v => v.Reference == accountRef, ct)
            ?? throw new NotFoundException($"Virtual account '{accountRef}' not found.");
        var hold = await db.EscrowHolds.FirstOrDefaultAsync(e => e.VirtualAccountId == account.Id && e.State == EscrowState.Held, ct)
            ?? throw new NotFoundException("No active escrow hold for this account.");
        hold.Release(clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }
}
