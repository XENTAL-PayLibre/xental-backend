using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

public sealed record TransactionFilter(
    DateTimeOffset? From, DateTimeOffset? To,
    TransactionStatus? Status, ReconciliationStatus? Reconciliation, string? AccountRef);

/// <summary>Tenant-scoped read model over the deposit transaction log (audit/statement).</summary>
public sealed class TransactionQueryService(IApplicationDbContext db, ITenantContext tenantContext)
{
    public async Task<IReadOnlyList<Transaction>> ListAsync(TransactionFilter f, int take, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var q = db.Transactions.AsNoTracking().Where(t => t.TenantId == tenantId);

        if (f.From is { } from) q = q.Where(t => t.OccurredAtUtc >= from);
        if (f.To is { } to) q = q.Where(t => t.OccurredAtUtc <= to);
        if (f.Status is { } s) q = q.Where(t => t.Status == s);
        if (f.Reconciliation is { } r) q = q.Where(t => t.Reconciliation == r);
        if (!string.IsNullOrWhiteSpace(f.AccountRef))
        {
            var reference = f.AccountRef.Trim();
            var vaIds = db.VirtualAccounts.Where(v => v.Reference == reference).Select(v => (Guid?)v.Id);
            q = q.Where(t => vaIds.Contains(t.VirtualAccountId));
        }

        return await q.OrderByDescending(t => t.OccurredAtUtc).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
    }

    public async Task<Transaction> GetByReferenceAsync(string reference, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var r = (reference ?? string.Empty).Trim();
        return await db.Transactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.NombaReference == r, ct)
            ?? throw new NotFoundException($"No transaction with reference '{r}'.");
    }
}
