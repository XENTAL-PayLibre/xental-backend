using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>Reconciliation + collections analytics for a tenant's dashboard.</summary>
public sealed record InsightsSummary(
    int VirtualAccounts,
    int Deposits,
    long TotalCollectedKobo,
    long ExpectedKobo,
    long OutstandingDeficitKobo,
    double CollectionRatePct,
    int Reconciled,
    int Underpaid,
    int Overpaid,
    int PendingReview,
    int HighRisk,
    int FullyPaidAccounts,
    int PartiallyPaidAccounts);

/// <summary>
/// Aggregates the reconciliation ledger into headline metrics: collection rate, outstanding
/// deficit, reconciliation breakdown, review/high-risk counts. Tenant-scoped.
/// </summary>
public sealed class InsightsService(IApplicationDbContext db, ITenantContext tenantContext)
{
    public async Task<InsightsSummary> GetAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var accounts = db.VirtualAccounts.AsNoTracking(); // tenant-filtered globally
        var txns = db.Transactions.AsNoTracking().Where(t => t.TenantId == tenantId);

        var vaCount = await accounts.CountAsync(ct);
        var expected = await accounts.Where(v => v.ExpectedAmountKobo != null)
            .SumAsync(v => (long?)v.ExpectedAmountKobo, ct) ?? 0;
        var paid = await accounts.SumAsync(v => (long?)v.AmountPaidKobo, ct) ?? 0;
        var deficit = await accounts.Where(v => v.ExpectedAmountKobo != null && v.AmountPaidKobo < v.ExpectedAmountKobo!.Value)
            .SumAsync(v => (long?)(v.ExpectedAmountKobo!.Value - v.AmountPaidKobo), ct) ?? 0;
        var fullyPaid = await accounts.CountAsync(v => v.PaymentState == PaymentState.FullyPaid, ct);
        var partiallyPaid = await accounts.CountAsync(v => v.PaymentState == PaymentState.PartiallyPaid, ct);

        var deposits = await txns.CountAsync(ct);
        var collected = await txns.SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
        var reconciled = await txns.CountAsync(t => t.Reconciliation == ReconciliationStatus.Reconciled, ct);
        var underpaid = await txns.CountAsync(t => t.Reconciliation == ReconciliationStatus.Underpaid, ct);
        var overpaid = await txns.CountAsync(t => t.Reconciliation == ReconciliationStatus.Overpaid, ct);
        var review = await txns.CountAsync(t => t.Reconciliation == ReconciliationStatus.PendingReview, ct);
        var highRisk = await txns.CountAsync(t => t.RiskScore >= RiskEvaluator.ReviewThreshold, ct);

        var rate = expected > 0 ? Math.Round(100.0 * paid / expected, 1) : 0;

        return new InsightsSummary(
            vaCount, deposits, collected, expected, deficit, rate,
            reconciled, underpaid, overpaid, review, highRisk, fullyPaid, partiallyPaid);
    }
}
