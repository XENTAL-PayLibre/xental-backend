using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Billing;
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

/// <summary>One age band of outstanding receivables.</summary>
public sealed record AgingBucket(string Label, int Accounts, long OutstandingKobo);
public sealed record AgingReport(long TotalOutstandingKobo, IReadOnlyList<AgingBucket> Buckets);

/// <summary>Expected scheduled inflows in one week of the forecast window.</summary>
public sealed record ForecastWeek(DateTimeOffset WeekStartUtc, long ScheduledKobo);
public sealed record CashFlowForecast(
    int Days, long ScheduledDueKobo, double DailyRunRateKobo,
    long RunRateProjectedKobo, long ProjectedTotalKobo, IReadOnlyList<ForecastWeek> Weeks);

/// <summary>A customer's collection reliability, scored 0–100.</summary>
public sealed record CustomerScore(
    string CustomerRef, string CustomerName, long ExpectedKobo, long PaidKobo, long OutstandingKobo,
    double CollectionRatePct, int Deposits, int DuePeriods, int LatePeriods, int Score, string Rating);

/// <summary>
/// Aggregates the reconciliation ledger into headline metrics: collection rate, outstanding
/// deficit, reconciliation breakdown, review/high-risk counts. Also powers Collections
/// Intelligence — receivables aging, a cash-flow forecast, and per-customer collection scores.
/// Tenant-scoped.
/// </summary>
public sealed class InsightsService(IApplicationDbContext db, ITenantContext tenantContext, IClock clock)
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

    /// <summary>Outstanding receivables bucketed by how long they've been outstanding (account age).</summary>
    public async Task<AgingReport> GetAgingAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var rows = await db.VirtualAccounts.AsNoTracking()
            .Where(v => v.ExpectedAmountKobo != null && v.AmountPaidKobo < v.ExpectedAmountKobo!.Value)
            .Select(v => new { v.CreatedAtUtc, Outstanding = v.ExpectedAmountKobo!.Value - v.AmountPaidKobo })
            .ToListAsync(ct);

        // Buckets in ascending age. Upper bound is exclusive; the last is open-ended.
        var defs = new (string Label, double MaxDays)[]
        {
            ("0–7 days", 7), ("8–30 days", 30), ("31–60 days", 60), ("60+ days", double.MaxValue),
        };
        var counts = new int[defs.Length];
        var sums = new long[defs.Length];
        foreach (var r in rows)
        {
            var ageDays = (now - r.CreatedAtUtc).TotalDays;
            for (var i = 0; i < defs.Length; i++)
            {
                if (ageDays <= defs[i].MaxDays) { counts[i]++; sums[i] += r.Outstanding; break; }
            }
        }

        var buckets = defs.Select((d, i) => new AgingBucket(d.Label, counts[i], sums[i])).ToList();
        return new AgingReport(sums.Sum(), buckets);
    }

    /// <summary>Cash-flow forecast over the next <paramref name="days"/>: scheduled billing due
    /// (weekly) plus an ad-hoc projection from the trailing 30-day deposit run-rate.</summary>
    public async Task<CashFlowForecast> GetForecastAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 7, 180);
        var tenantId = tenantContext.RequireTenantId();
        var now = clock.UtcNow;
        var horizon = now.AddDays(days);

        var duePeriods = await db.BillingPeriods.AsNoTracking()
            .Where(p => p.Status != BillingPeriodStatus.Paid && p.DueDateUtc >= now && p.DueDateUtc <= horizon)
            .Select(p => new { p.DueDateUtc, Outstanding = p.ExpectedAmountKobo - p.AmountAttributedKobo })
            .ToListAsync(ct);

        var weekCount = (int)Math.Ceiling(days / 7.0);
        var weeks = new List<ForecastWeek>(weekCount);
        for (var w = 0; w < weekCount; w++)
        {
            var start = now.AddDays(7 * w);
            var end = now.AddDays(7 * (w + 1));
            var scheduled = duePeriods
                .Where(p => p.DueDateUtc >= start && p.DueDateUtc < end)
                .Sum(p => Math.Max(0, p.Outstanding));
            weeks.Add(new ForecastWeek(start, scheduled));
        }
        var scheduledDue = weeks.Sum(x => x.ScheduledKobo);

        // Trailing 30-day run-rate of ad-hoc collections (deposits not tied to a schedule average out here).
        var since = now.AddDays(-30);
        var recent = await db.Transactions.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.OccurredAtUtc >= since)
            .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
        var dailyRunRate = recent / 30.0;
        var runRateProjected = (long)(dailyRunRate * days);

        return new CashFlowForecast(days, scheduledDue, Math.Round(dailyRunRate, 0),
            runRateProjected, scheduledDue + runRateProjected, weeks);
    }

    /// <summary>Per-customer collection reliability, worst (most outstanding) first.</summary>
    public async Task<IReadOnlyList<CustomerScore>> GetCustomerScoresAsync(int take = 100, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var now = clock.UtcNow;

        var customers = await db.Customers.AsNoTracking().ToListAsync(ct);
        var accounts = await db.VirtualAccounts.AsNoTracking().ToListAsync(ct);
        var schedules = await db.BillingSchedules.AsNoTracking().ToListAsync(ct);
        var periods = await db.BillingPeriods.AsNoTracking().Where(p => p.TenantId == tenantId).ToListAsync(ct);
        var deposits = await db.Transactions.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.VirtualAccountId != null)
            .GroupBy(t => t.VirtualAccountId!.Value)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var depositByAccount = deposits.ToDictionary(d => d.AccountId, d => d.Count);
        var scheduleCustomer = schedules.ToDictionary(s => s.Id, s => s.CustomerId);

        var scores = new List<CustomerScore>();
        foreach (var c in customers)
        {
            var custAccounts = accounts.Where(a => a.CustomerId == c.Id).ToList();
            if (custAccounts.Count == 0) continue;

            var expected = custAccounts.Sum(a => a.ExpectedAmountKobo ?? 0);
            var paid = custAccounts.Sum(a => a.AmountPaidKobo);
            var outstanding = custAccounts.Sum(a => Math.Max(0, (a.ExpectedAmountKobo ?? 0) - a.AmountPaidKobo));
            var depositCount = custAccounts.Sum(a => depositByAccount.GetValueOrDefault(a.Id));

            var custPeriods = periods.Where(p => scheduleCustomer.TryGetValue(p.BillingScheduleId, out var cid) && cid == c.Id).ToList();
            var duePeriods = custPeriods.Where(p => p.DueDateUtc <= now).ToList();
            var late = duePeriods.Count(p => p.Status != BillingPeriodStatus.Paid && (p.ExpectedAmountKobo - p.AmountAttributedKobo) > 0);
            var onTime = duePeriods.Count(p => p.Status == BillingPeriodStatus.Paid && p.PaidAtUtc.HasValue && p.PaidAtUtc.Value <= p.DueDateUtc);

            var collectionRate = expected > 0 ? Math.Min(100.0, 100.0 * paid / expected) : 100.0;
            var punctuality = duePeriods.Count > 0 ? 100.0 * onTime / duePeriods.Count : collectionRate;
            var score = (int)Math.Round(Math.Clamp(0.6 * collectionRate + 0.4 * punctuality, 0, 100));
            var rating = score >= 85 ? "Excellent" : score >= 70 ? "Good" : score >= 50 ? "Fair" : "Poor";

            scores.Add(new CustomerScore(
                c.Reference, c.Name, expected, paid, outstanding,
                Math.Round(collectionRate, 1), depositCount, duePeriods.Count, late, score, rating));
        }

        return scores
            .OrderByDescending(s => s.OutstandingKobo).ThenBy(s => s.Score)
            .Take(Math.Clamp(take, 1, 500))
            .ToList();
    }
}
