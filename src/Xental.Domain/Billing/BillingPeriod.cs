using Xental.Domain.Common;

namespace Xental.Domain.Billing;

public enum BillingPeriodStatus { Open = 1, PartiallyPaid = 2, Paid = 3, Overdue = 4 }

/// <summary>
/// One billing cycle of a <see cref="BillingSchedule"/>: an expected amount owed between
/// <see cref="PeriodStartUtc"/> and <see cref="PeriodEndUtc"/>, due by <see cref="DueDateUtc"/>.
/// Inflows into the schedule's DVA are attributed here; when the attributed total meets the
/// expected amount the period is Paid. Never holds money itself — it is an accounting record.
/// </summary>
public sealed class BillingPeriod : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid BillingScheduleId { get; private set; }
    public int Sequence { get; private set; }

    public DateTimeOffset PeriodStartUtc { get; private set; }
    public DateTimeOffset PeriodEndUtc { get; private set; }
    public DateTimeOffset DueDateUtc { get; private set; }

    public long ExpectedAmountKobo { get; private set; }
    public long AmountAttributedKobo { get; private set; }

    public BillingPeriodStatus Status { get; private set; }
    public DateTimeOffset? PaidAtUtc { get; private set; }

    /// <summary>The "payment due" reminder has been sent for this period.</summary>
    public bool DueNotified { get; private set; }
    /// <summary>The "payment overdue" reminder has been sent for this period.</summary>
    public bool OverdueNotified { get; private set; }

    private BillingPeriod() { } // EF

    public BillingPeriod(
        Guid tenantId, Guid billingScheduleId, int sequence,
        DateTimeOffset periodStartUtc, DateTimeOffset periodEndUtc, DateTimeOffset dueDateUtc, long expectedAmountKobo)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (billingScheduleId == Guid.Empty) throw new DomainException("BillingScheduleId is required.");
        if (expectedAmountKobo <= 0) throw new DomainException("Expected amount must be positive.");

        TenantId = tenantId;
        BillingScheduleId = billingScheduleId;
        Sequence = sequence;
        PeriodStartUtc = periodStartUtc;
        PeriodEndUtc = periodEndUtc;
        DueDateUtc = dueDateUtc;
        ExpectedAmountKobo = expectedAmountKobo;
        Status = BillingPeriodStatus.Open;
    }

    public long OutstandingKobo => Math.Max(0, ExpectedAmountKobo - AmountAttributedKobo);
    public bool IsSettled => Status == BillingPeriodStatus.Paid;

    /// <summary>When this period tips into "overdue" if still unpaid. If the schedule set a due
    /// offset (due date after the start) that date governs; otherwise the payer has the whole cycle,
    /// so it is only overdue once the period has fully elapsed.</summary>
    public DateTimeOffset OverdueLineUtc => DueDateUtc > PeriodStartUtc ? DueDateUtc : PeriodEndUtc;

    /// <summary>
    /// Draw down up to <paramref name="available"/> kobo to cover this period. Returns the amount
    /// consumed (0 if already paid). Transitions the status and stamps <see cref="PaidAtUtc"/> when
    /// fully covered. Returns whether this call was the one that settled the period (for events).
    /// </summary>
    public long Attribute(long available, DateTimeOffset now, out bool justPaid)
    {
        justPaid = false;
        if (available <= 0 || Status == BillingPeriodStatus.Paid)
            return 0;

        var take = Math.Min(OutstandingKobo, available);
        if (take <= 0)
            return 0;

        AmountAttributedKobo += take;
        if (AmountAttributedKobo >= ExpectedAmountKobo)
        {
            Status = BillingPeriodStatus.Paid;
            PaidAtUtc = now;
            justPaid = true;
        }
        else
        {
            Status = BillingPeriodStatus.PartiallyPaid;
        }
        return take;
    }

    /// <summary>Clear attribution so a reversal can redistribute the account's now-lower balance.
    /// Returns true if the period was previously settled (Paid) and has now been re-opened, so the
    /// caller can notify. Keeps the due/overdue notification flags (the payer was already reminded).</summary>
    public bool ResetAttribution()
    {
        var wasPaid = Status == BillingPeriodStatus.Paid;
        AmountAttributedKobo = 0;
        PaidAtUtc = null;
        Status = BillingPeriodStatus.Open;
        return wasPaid;
    }

    /// <summary>Mark this period overdue (past due date, still unpaid). No-op once paid.</summary>
    public bool MarkOverdue()
    {
        if (Status == BillingPeriodStatus.Paid || Status == BillingPeriodStatus.Overdue)
            return false;
        Status = BillingPeriodStatus.Overdue;
        return true;
    }

    public void MarkDueNotified() => DueNotified = true;
    public void MarkOverdueNotified() => OverdueNotified = true;
}
