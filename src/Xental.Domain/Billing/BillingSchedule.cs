using Xental.Domain.Common;

namespace Xental.Domain.Billing;

/// <summary>How often a billing schedule opens a new period.</summary>
public enum BillingInterval { Weekly = 1, Monthly = 2, Quarterly = 3, Yearly = 4 }

public enum BillingScheduleStatus { Active = 1, Paused = 2, Cancelled = 3 }

/// <summary>Date arithmetic for advancing by one <see cref="BillingInterval"/>.</summary>
public static class BillingIntervalExtensions
{
    public static DateTimeOffset Advance(this BillingInterval interval, DateTimeOffset from) => interval switch
    {
        BillingInterval.Weekly => from.AddDays(7),
        BillingInterval.Monthly => from.AddMonths(1),
        BillingInterval.Quarterly => from.AddMonths(3),
        BillingInterval.Yearly => from.AddYears(1),
        _ => from.AddMonths(1),
    };
}

/// <summary>
/// A recurring-billing plan bound to a reusable virtual account (the customer's DVA). Each cycle
/// the dunning worker opens a <see cref="BillingPeriod"/> for the schedule's current expected amount
/// (which the merchant can vary per cycle), and inflows into the DVA are attributed to open periods
/// oldest-first. This is the push model: the customer pays into their DVA; Xental attributes and
/// reminds. It never pulls funds (that would be auto-debit, which is out of scope).
/// </summary>
public sealed class BillingSchedule : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid VirtualAccountId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string Reference { get; private set; } = null!;        // unique per tenant
    public string? Description { get; private set; }

    public BillingInterval Interval { get; private set; }
    public BillingScheduleStatus Status { get; private set; }

    /// <summary>Expected amount (kobo) used when the next period opens. Variable — the merchant
    /// can change it between cycles.</summary>
    public long NextAmountKobo { get; private set; }

    /// <summary>Days after a period starts that its payment falls due (0 = due on open).</summary>
    public int DueOffsetDays { get; private set; }

    /// <summary>Number of periods opened so far — also the next period's sequence.</summary>
    public int PeriodsGenerated { get; private set; }

    /// <summary>End of the most recently opened period; the worker opens the next one once now passes it.</summary>
    public DateTimeOffset? CurrentPeriodEndUtc { get; private set; }

    /// <summary>Overpayment pool (kobo) not yet applied to a period — applied when the next period opens.</summary>
    public long CarryCreditKobo { get; private set; }

    /// <summary>Water-mark of the DVA's cumulative AmountPaidKobo already attributed to periods.</summary>
    public long AttributedUpToKobo { get; private set; }

    private BillingSchedule() { } // EF

    public BillingSchedule(
        Guid tenantId, Guid virtualAccountId, Guid customerId, string reference,
        BillingInterval interval, long firstAmountKobo, int dueOffsetDays = 0, string? description = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (virtualAccountId == Guid.Empty) throw new DomainException("VirtualAccountId is required.");
        if (customerId == Guid.Empty) throw new DomainException("CustomerId is required.");
        if (firstAmountKobo <= 0) throw new DomainException("Billing amount must be positive.");
        if (dueOffsetDays < 0) throw new DomainException("Due offset cannot be negative.");

        TenantId = tenantId;
        VirtualAccountId = virtualAccountId;
        CustomerId = customerId;
        Reference = DomainException.Require(reference, nameof(reference));
        Interval = interval;
        Status = BillingScheduleStatus.Active;
        NextAmountKobo = firstAmountKobo;
        DueOffsetDays = dueOffsetDays;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public bool IsActive => Status == BillingScheduleStatus.Active;

    /// <summary>Set the expected amount for the next cycle to open (variable billing).</summary>
    public void SetNextAmount(long amountKobo)
    {
        if (amountKobo <= 0) throw new DomainException("Billing amount must be positive.");
        NextAmountKobo = amountKobo;
    }

    public void Pause() { if (Status == BillingScheduleStatus.Active) Status = BillingScheduleStatus.Paused; }
    public void Resume() { if (Status == BillingScheduleStatus.Paused) Status = BillingScheduleStatus.Active; }
    public void Cancel() => Status = BillingScheduleStatus.Cancelled;

    /// <summary>Whether a new period should be opened now (active, and either none opened yet or the
    /// current one has elapsed).</summary>
    public bool ShouldOpenPeriod(DateTimeOffset now) =>
        IsActive && (CurrentPeriodEndUtc is null || now >= CurrentPeriodEndUtc.Value);

    /// <summary>Open the next period and advance the schedule's counters. The caller persists the
    /// returned period, then runs attribution — which applies any carried-over credit to it. Carry is
    /// owned entirely by attribution, so this does not touch it.
    ///
    /// If the schedule is behind (paused for months then resumed, or the worker was offline), cycles
    /// that have already fully elapsed are skipped rather than materialized one-per-tick — otherwise the
    /// worker would emit a back-dated period, a "due" email, and an immediate "overdue" email for every
    /// missed cycle. Only the current live period is opened.</summary>
    public BillingPeriod OpenNextPeriod(DateTimeOffset now)
    {
        var start = CurrentPeriodEndUtc is { } end && end > DateTimeOffset.UnixEpoch ? end : now;
        // Fast-forward over fully-elapsed cycles (guard bounds ~a century of weekly cycles).
        var guard = 0;
        while (Interval.Advance(start) <= now && guard++ < 6000)
            start = Interval.Advance(start);
        var periodEnd = Interval.Advance(start);
        var due = DueOffsetDays > 0 ? start.AddDays(DueOffsetDays) : start;

        var period = new BillingPeriod(
            TenantId, Id, PeriodsGenerated + 1, start, periodEnd, due, NextAmountKobo);

        PeriodsGenerated += 1;
        CurrentPeriodEndUtc = periodEnd;
        return period;
    }

    /// <summary>Record the result of an attribution pass: advance the attributed water-mark and park
    /// any remaining (unapplied) credit for the next period.</summary>
    public void RecordAttribution(long attributedUpToKobo, long leftoverCreditKobo)
    {
        AttributedUpToKobo = Math.Max(AttributedUpToKobo, attributedUpToKobo);
        CarryCreditKobo = Math.Max(0, leftoverCreditKobo);
    }

    /// <summary>Set the attributed water-mark directly (allowing it to move *down*). Used only when a
    /// reversal lowers the account balance and periods are redistributed from scratch.</summary>
    public void ResetAttribution(long attributedUpToKobo, long leftoverCreditKobo)
    {
        AttributedUpToKobo = Math.Max(0, attributedUpToKobo);
        CarryCreditKobo = Math.Max(0, leftoverCreditKobo);
    }

    /// <summary>Draw a refunded overpayment out of the carried-forward credit so it does not pre-pay the
    /// next cycle. Returns the amount actually removed (bounded by the available carry).</summary>
    public long ReduceCarry(long kobo)
    {
        var removed = Math.Min(Math.Max(0, kobo), CarryCreditKobo);
        CarryCreditKobo -= removed;
        return removed;
    }
}
