using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;
using Xental.Domain.Payments;

namespace Xental.Application.Admin;

public sealed record ReconTransactionView(
    Guid Id, Guid? TenantId, Guid? VirtualAccountId, string Reference,
    long AmountKobo, long NetCreditKobo, string Status, string Reconciliation, string? Reason,
    int RiskScore, string? TransferName, DateTimeOffset OccurredAtUtc);

public sealed record ReconSummary(int Review, int Unknown, int Overpaid, int Underpaid, int HighRisk, int Reversals);

public sealed record FailedSettlementView(
    Guid VirtualAccountId, Guid TenantId, string AccountRef, long NetKobo, string? FailureReason, DateTimeOffset? FailedAtUtc);

/// <summary>
/// Read surface + settlement ops over the reconciliation engine's own output. Cross-tenant (the
/// ledger isn't tenant-filtered) and audited. The transaction ledger is immutable by design, so the
/// console exposes the exception buckets for triage; the one mutating op is retrying a failed
/// auto-settlement (clears the failed sweep so the settlement worker re-attempts it).
/// </summary>
public sealed class AdminReconciliationService(IApplicationDbContext db, IAdminContext admin, IClock clock)
{
    private const int RiskThreshold = 70;

    public async Task<IReadOnlyList<ReconTransactionView>> ListAsync(string bucket, int take = 200, CancellationToken ct = default)
    {
        var q = db.Transactions.AsNoTracking();
        q = bucket?.ToLowerInvariant() switch
        {
            "review" => q.Where(t => t.Reconciliation == ReconciliationStatus.PendingReview),
            "unknown" => q.Where(t => t.Reason == TransactionFlag.InvalidAccount),
            "overpaid" => q.Where(t => t.Reconciliation == ReconciliationStatus.Overpaid),
            "underpaid" => q.Where(t => t.Reconciliation == ReconciliationStatus.Underpaid),
            "highrisk" => q.Where(t => t.RiskScore >= RiskThreshold),
            "reversals" => q.Where(t => t.Reconciliation == ReconciliationStatus.Reversed),
            _ => throw new ValidationException("Unknown bucket. Use review|unknown|overpaid|underpaid|highrisk|reversals."),
        };
        var items = await q.OrderByDescending(t => t.OccurredAtUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return items.Select(Map).ToList();
    }

    public async Task<ReconSummary> SummaryAsync(CancellationToken ct = default)
    {
        var q = db.Transactions.AsNoTracking();
        return new ReconSummary(
            Review: await q.CountAsync(t => t.Reconciliation == ReconciliationStatus.PendingReview, ct),
            Unknown: await q.CountAsync(t => t.Reason == TransactionFlag.InvalidAccount, ct),
            Overpaid: await q.CountAsync(t => t.Reconciliation == ReconciliationStatus.Overpaid, ct),
            Underpaid: await q.CountAsync(t => t.Reconciliation == ReconciliationStatus.Underpaid, ct),
            HighRisk: await q.CountAsync(t => t.RiskScore >= RiskThreshold, ct),
            Reversals: await q.CountAsync(t => t.Reconciliation == ReconciliationStatus.Reversed, ct));
    }

    /// <summary>Fully-paid accounts whose auto-settlement sweep failed and awaits a retry.</summary>
    public async Task<IReadOnlyList<FailedSettlementView>> ListFailedSettlementsAsync(CancellationToken ct = default)
    {
        var failed = await db.Transfers.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Status == TransferStatus.Failed && t.MerchantTxRef.StartsWith("settle-"))
            .ToListAsync(ct);
        if (failed.Count == 0) return [];

        var vaIds = failed.Select(f => Guid.TryParse(f.MerchantTxRef["settle-".Length..], out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToList();
        var vas = await db.VirtualAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(v => vaIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);

        var result = new List<FailedSettlementView>();
        foreach (var f in failed)
        {
            if (!Guid.TryParse(f.MerchantTxRef["settle-".Length..], out var vaId) || !vas.TryGetValue(vaId, out var va)) continue;
            result.Add(new FailedSettlementView(vaId, va.TenantId, va.Reference, f.AmountKobo, f.FailureReason, f.CompletedAtUtc));
        }
        return result;
    }

    /// <summary>Clear a failed settlement sweep so the settlement worker re-attempts it next cycle.</summary>
    public async Task RetrySettlementAsync(Guid virtualAccountId, CancellationToken ct = default)
    {
        var merchantRef = $"settle-{virtualAccountId:N}";
        var transfer = await db.Transfers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef && t.Status == TransferStatus.Failed, ct)
            ?? throw new NotFoundException("No failed settlement found for that account.");
        db.Transfers.Remove(transfer);
        db.AdminAuditLogs.Add(new AdminAuditLog(admin.RequireAdminId(), "retry_settlement", transfer.TenantId.ToString(), merchantRef, clock.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    private static ReconTransactionView Map(Transaction t) => new(
        t.Id, t.TenantId, t.VirtualAccountId, t.NombaReference, t.AmountKobo, t.NetCreditKobo,
        t.Status.ToString(), t.Reconciliation.ToString(), t.Reason?.ToString(), t.RiskScore, t.TransferName, t.OccurredAtUtc);
}
