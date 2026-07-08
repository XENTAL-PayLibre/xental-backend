using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

public sealed record TransactionFilter(
    DateTimeOffset? From, DateTimeOffset? To,
    TransactionStatus? Status, ReconciliationStatus? Reconciliation, string? AccountRef);

/// <summary>Pay-ins summary for the dashboard cards (optionally scoped to a date range).</summary>
public sealed record TransactionSummary(
    int Total,
    long TotalPayinsKobo,
    int Successful,
    int Failed,
    int PendingReview,
    long SuccessfulKobo,
    long NetCreditedKobo);

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

    /// <summary>Aggregate pay-ins stats for the dashboard (total, successful, failed), optionally in a
    /// date range. "Successful" = a credited deposit that wasn't reversed; "Failed" = a reversal.</summary>
    public async Task<TransactionSummary> SummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var q = db.Transactions.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (from is { } f) q = q.Where(t => t.OccurredAtUtc >= f);
        if (to is { } tt) q = q.Where(t => t.OccurredAtUtc <= tt);

        // One grouped round-trip: count + sum per (Status, Reconciliation), then fold in memory.
        var rows = await q
            .GroupBy(t => new { t.Status, t.Reconciliation })
            .Select(g => new { g.Key.Status, g.Key.Reconciliation, Count = g.Count(), Gross = g.Sum(x => x.AmountKobo), Net = g.Sum(x => x.NetCreditKobo) })
            .ToListAsync(ct);

        var total = rows.Sum(r => r.Count);
        var reversedCount = rows.Where(r => r.Reconciliation == ReconciliationStatus.Reversed).Sum(r => r.Count);
        var failed = rows.Where(r => r.Status == TransactionStatus.Failed || r.Reconciliation == ReconciliationStatus.Reversed).Sum(r => r.Count);
        var successful = rows.Where(r => r.Status == TransactionStatus.Success && r.Reconciliation != ReconciliationStatus.Reversed).Sum(r => r.Count);
        var pendingReview = rows.Where(r => r.Reconciliation == ReconciliationStatus.PendingReview).Sum(r => r.Count);

        // Money in = gross of non-reversed deposits; net credited subtracts provider fees and reversals.
        var grossIn = rows.Where(r => r.Reconciliation != ReconciliationStatus.Reversed).Sum(r => r.Gross);
        var successfulKobo = rows.Where(r => r.Status == TransactionStatus.Success && r.Reconciliation != ReconciliationStatus.Reversed).Sum(r => r.Gross);
        var netCredited = rows.Where(r => r.Reconciliation != ReconciliationStatus.Reversed).Sum(r => r.Net)
                        - rows.Where(r => r.Reconciliation == ReconciliationStatus.Reversed).Sum(r => r.Net);

        return new TransactionSummary(total, grossIn, successful, failed, pendingReview, successfulKobo, netCredited);
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
