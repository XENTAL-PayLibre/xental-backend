using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Merchants;

namespace Xental.Application.Merchants;

/// <summary>Collected / settled / pending balance for a sub-merchant, in net kobo.</summary>
public sealed record SubMerchantBalance(
    Guid SubMerchantId, string Reference, long CollectedKobo, long SettledKobo, long PendingKobo, int VirtualAccounts);

/// <summary>
/// Creates and manages sub-merchants for the current tenant, including their payout account (used to
/// route settlement) and per-sub-merchant balances. A sub-merchant is an internal Xental record (no
/// Nomba object): collection happens through the operator's platform Nomba account and funds are
/// attributed via Xental's own ledger (the immutable transaction records).
/// </summary>
public sealed class SubMerchantService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    INombaClient nomba)
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

    public async Task<SubMerchant> GetAsync(Guid id, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        return await db.SubMerchants.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException($"Sub-merchant '{id}' not found.");
    }

    /// <summary>Set the payout account + platform fee. Verifies the account with a NUBAN name-match and
    /// stores the bank-verified account name.</summary>
    public async Task<SubMerchant> SetPayoutAsync(
        Guid id, string bankName, string bankCode, string accountNumber, int platformFeeBps, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        var sub = await db.SubMerchants.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException($"Sub-merchant '{id}' not found.");

        string verifiedName;
        try
        {
            var lookup = await nomba.LookupBankAccountAsync(accountNumber.Trim(), bankCode.Trim(), ct);
            verifiedName = lookup.AccountName;
        }
        catch (NombaIntegrationException ex)
        {
            throw new ValidationException($"Could not verify the payout account. Check the account number and bank code. ({ex.Message})");
        }

        sub.SetPayout(bankName, bankCode, accountNumber, verifiedName, platformFeeBps);
        await db.SaveChangesAsync(ct);
        return sub;
    }

    /// <summary>Net collected, settled, and pending for a sub-merchant (kobo), across all its virtual accounts.</summary>
    public async Task<SubMerchantBalance> GetBalanceAsync(Guid id, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        var sub = await db.SubMerchants.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException($"Sub-merchant '{id}' not found.");

        var vas = await db.VirtualAccounts.AsNoTracking()
            .Where(v => v.SubMerchantId == id)
            .Select(v => new { v.Id, v.SettledUpToKobo })
            .ToListAsync(ct);

        var vaIds = vas.Select(v => v.Id).ToList();
        long collected = 0L;
        if (vaIds.Count > 0)
        {
            // Net collected = credited inflows minus reversed ones (a reversal is stored with a positive
            // NetCreditKobo, so it must be subtracted rather than summed in).
            var credited = await db.Transactions.AsNoTracking()
                .Where(t => t.VirtualAccountId != null && vaIds.Contains(t.VirtualAccountId.Value)
                    && t.Reconciliation != Xental.Domain.Payments.ReconciliationStatus.Reversed)
                .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0L;
            var reversed = await db.Transactions.AsNoTracking()
                .Where(t => t.VirtualAccountId != null && vaIds.Contains(t.VirtualAccountId.Value)
                    && t.Reconciliation == Xental.Domain.Payments.ReconciliationStatus.Reversed)
                .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0L;
            collected = credited - reversed;
        }

        var settled = vas.Sum(v => v.SettledUpToKobo);
        return new SubMerchantBalance(sub.Id, sub.Reference, collected, settled, Math.Max(0, collected - settled), vas.Count);
    }
}
