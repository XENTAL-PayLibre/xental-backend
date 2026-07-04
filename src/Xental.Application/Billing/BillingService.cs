using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Webhooks;
using Xental.Domain.Billing;
using Xental.Domain.Payments;

namespace Xental.Application.Billing;

/// <summary>A schedule paired with its virtual account's developer-facing reference (for responses).</summary>
public sealed record BillingScheduleView(BillingSchedule Schedule, string AccountRef);

/// <summary>
/// Recurring billing (push model): a schedule bound to a customer's reusable DVA opens one period
/// per cycle for a (variable) expected amount. Inflows into the DVA are attributed to open periods
/// oldest-first by <see cref="AttributeDepositAsync"/>, which the reconciliation engine calls after
/// crediting a deposit. Period generation, reminders, and overdue detection are driven by the
/// billing worker. No funds are ever pulled — the customer pushes; Xental attributes and reminds.
/// </summary>
public sealed class BillingService(
    IApplicationDbContext db, ITenantContext tenantContext, OutboundEventPublisher outbound, IEmailSender email,
    IErrorAlerter alerter, IClock clock)
{
    /// <summary>Create a schedule on one of the tenant's reusable virtual accounts and open its first period.</summary>
    public async Task<BillingScheduleView> CreateAsync(
        string accountRef, BillingInterval interval, long firstAmountKobo,
        int dueOffsetDays = 0, string? description = null, string? reference = null, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        if (firstAmountKobo <= 0)
            throw new ValidationException("amountKobo must be positive.");

        var accRef = (accountRef ?? string.Empty).Trim();
        var account = await db.VirtualAccounts.FirstOrDefaultAsync(v => v.Reference == accRef, ct)
            ?? throw new NotFoundException($"No virtual account for accountRef '{accRef}'.");
        if (account.Status != VirtualAccountStatus.Active)
            throw new ValidationException("Cannot schedule billing on a closed virtual account.");

        var scheduleRef = string.IsNullOrWhiteSpace(reference) ? "bsch_" + Guid.NewGuid().ToString("N")[..16] : reference.Trim();
        if (await db.BillingSchedules.AnyAsync(s => s.Reference == scheduleRef, ct))
            throw new ConflictException($"A billing schedule already exists with reference '{scheduleRef}'.");
        if (await db.BillingSchedules.AnyAsync(s => s.VirtualAccountId == account.Id && s.Status != BillingScheduleStatus.Cancelled, ct))
            throw new ConflictException($"Virtual account '{accRef}' already has an active billing schedule.");

        var schedule = new BillingSchedule(
            account.TenantId, account.Id, account.CustomerId, scheduleRef, interval, firstAmountKobo, dueOffsetDays, description);
        db.BillingSchedules.Add(schedule);
        try
        {
            await db.SaveChangesAsync(ct); // materialize Id before opening a period
        }
        catch (DbUpdateException) // partial unique index caught a concurrent create (ref or one-per-DVA)
        {
            throw new ConflictException($"A billing schedule already exists for accountRef '{accRef}'.");
        }

        // Open the first period immediately so the schedule is usable at once, then attribute any
        // balance the DVA already carries.
        var period = schedule.OpenNextPeriod(clock.UtcNow);
        db.BillingPeriods.Add(period);
        await db.SaveChangesAsync(ct);
        await AttributeInternalAsync(schedule, account, ct);
        await db.SaveChangesAsync(ct);
        return new BillingScheduleView(schedule, account.Reference);
    }

    public async Task<BillingScheduleView> SetNextAmountAsync(Guid scheduleId, long amountKobo, CancellationToken ct = default)
    {
        var schedule = await RequireScheduleAsync(scheduleId, ct);
        if (amountKobo <= 0) throw new ValidationException("amountKobo must be positive.");
        schedule.SetNextAmount(amountKobo);
        await db.SaveChangesAsync(ct);
        return await ToViewAsync(schedule, ct);
    }

    public async Task<BillingScheduleView> PauseAsync(Guid scheduleId, CancellationToken ct = default)
    {
        var s = await RequireScheduleAsync(scheduleId, ct); s.Pause(); await db.SaveChangesAsync(ct); return await ToViewAsync(s, ct);
    }

    public async Task<BillingScheduleView> ResumeAsync(Guid scheduleId, CancellationToken ct = default)
    {
        var s = await RequireScheduleAsync(scheduleId, ct); s.Resume(); await db.SaveChangesAsync(ct); return await ToViewAsync(s, ct);
    }

    public async Task<BillingScheduleView> CancelAsync(Guid scheduleId, CancellationToken ct = default)
    {
        var s = await RequireScheduleAsync(scheduleId, ct); s.Cancel(); await db.SaveChangesAsync(ct); return await ToViewAsync(s, ct);
    }

    public async Task<BillingScheduleView> GetAsync(Guid scheduleId, CancellationToken ct = default) =>
        await ToViewAsync(await RequireScheduleAsync(scheduleId, ct), ct);

    public async Task<IReadOnlyList<BillingScheduleView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        var schedules = await db.BillingSchedules.AsNoTracking()
            .OrderByDescending(s => s.CreatedAtUtc).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        if (schedules.Count == 0)
            return Array.Empty<BillingScheduleView>();

        var ids = schedules.Select(s => s.VirtualAccountId).ToList();
        var refs = await db.VirtualAccounts.AsNoTracking()
            .Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Reference, ct);
        return schedules.Select(s => new BillingScheduleView(s, refs.GetValueOrDefault(s.VirtualAccountId, ""))).ToList();
    }

    private async Task<BillingScheduleView> ToViewAsync(BillingSchedule schedule, CancellationToken ct)
    {
        var accRef = await db.VirtualAccounts.AsNoTracking()
            .Where(v => v.Id == schedule.VirtualAccountId).Select(v => v.Reference).FirstOrDefaultAsync(ct) ?? "";
        return new BillingScheduleView(schedule, accRef);
    }

    public async Task<IReadOnlyList<BillingPeriod>> ListPeriodsAsync(Guid scheduleId, int take = 100, CancellationToken ct = default)
    {
        await RequireScheduleAsync(scheduleId, ct); // tenant-scoped existence check
        return await db.BillingPeriods.AsNoTracking()
            .Where(p => p.BillingScheduleId == scheduleId)
            .OrderByDescending(p => p.Sequence).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    private async Task<BillingSchedule> RequireScheduleAsync(Guid scheduleId, CancellationToken ct)
    {
        tenantContext.RequireTenantId();
        return await db.BillingSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct)
            ?? throw new NotFoundException($"Billing schedule '{scheduleId}' not found.");
    }

    /// <summary>
    /// Attribute the DVA's newly-credited balance to its schedule's open periods (oldest first),
    /// carrying any overpayment forward. Called by the reconciliation engine post-commit, so it runs
    /// without a tenant context and ignores query filters. Saves its own unit of work; idempotent via
    /// the schedule's attributed water-mark.
    /// </summary>
    public async Task AttributeDepositAsync(Guid virtualAccountId, CancellationToken ct = default)
    {
        var account = await db.VirtualAccounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == virtualAccountId, ct);
        if (account is null) return;

        var schedule = await db.BillingSchedules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.VirtualAccountId == virtualAccountId && s.Status == BillingScheduleStatus.Active, ct);
        if (schedule is null) return;

        if (await AttributeInternalAsync(schedule, account, ct))
            await db.SaveChangesAsync(ct);
    }

    /// <summary>Worker backstop: attribute any deposits the webhook path missed (e.g. lost an xmin race
    /// with the billing worker). Idempotent via the water-mark, so it's a no-op once caught up. Runs
    /// filter-free without a tenant context. Returns the number of schedules that moved.</summary>
    public async Task<int> AttributePendingAsync(CancellationToken ct = default)
    {
        var vaIds = await db.BillingSchedules.IgnoreQueryFilters()
            .Where(s => s.Status == BillingScheduleStatus.Active)
            .OrderBy(s => s.CreatedAtUtc)
            .Select(s => s.VirtualAccountId).Take(500).ToListAsync(ct);

        var moved = 0;
        foreach (var vaId in vaIds)
        {
            try { await AttributeDepositAsync(vaId, ct); moved++; }
            catch (DbUpdateConcurrencyException) { /* lost a race; the next tick retries from the water-mark */ }
        }
        return moved;
    }

    /// <summary>Re-attribute after a deposit reversal: the DVA balance has dropped, so redistribute the
    /// account's current (lower) total across periods oldest-first and re-open any period that is no
    /// longer covered. Emits <c>billing.period.reopened</c> for periods that flipped out of Paid, and
    /// raises an operational alert when funds had already been settled out (possible clawback needed).
    /// Best-effort, called post-commit from the reconciliation reversal branch.</summary>
    public async Task ReattributeAfterReversalAsync(Guid virtualAccountId, CancellationToken ct = default)
    {
        var account = await db.VirtualAccounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == virtualAccountId, ct);
        if (account is null) return;

        // If we had already settled funds out and a deposit just reversed, we may now be short.
        if (account.SettledUpToKobo > 0)
            await alerter.NotifyOperationalAsync("Deposit reversed after settlement",
                $"A deposit into {account.Reference} was reversed after {account.SettledUpToKobo} kobo had already been settled out. Balance is now {account.AmountPaidKobo} kobo — verify whether a clawback is required.",
                $"reversal-{account.Id}", ct);

        var schedule = await db.BillingSchedules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.VirtualAccountId == virtualAccountId && s.Status == BillingScheduleStatus.Active, ct);
        if (schedule is null) return;

        var periods = await db.BillingPeriods.IgnoreQueryFilters()
            .Where(p => p.BillingScheduleId == schedule.Id)
            .OrderBy(p => p.Sequence).ToListAsync(ct);

        var reopened = new List<BillingPeriod>();
        foreach (var period in periods)
            if (period.ResetAttribution())
                reopened.Add(period);

        // Redistribute the account's current total from scratch, oldest-first.
        var now = clock.UtcNow;
        var pool = account.AmountPaidKobo;
        foreach (var period in periods)
        {
            if (pool <= 0) break;
            pool -= period.Attribute(pool, now, out _);
        }
        schedule.ResetAttribution(account.AmountPaidKobo, pool);

        foreach (var period in reopened.Where(p => p.Status != BillingPeriodStatus.Paid))
            await outbound.PublishEventAsync(schedule.TenantId, "billing.period.reopened", PeriodEventData(schedule, account, period), ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Core attribution against already-loaded entities. Enqueues billing.period.paid events
    /// for periods it settles. Does not save. Returns whether anything changed.</summary>
    private async Task<bool> AttributeInternalAsync(BillingSchedule schedule, VirtualAccount account, CancellationToken ct)
    {
        var delta = account.AmountPaidKobo - schedule.AttributedUpToKobo;
        var pool = schedule.CarryCreditKobo + Math.Max(0, delta);
        if (pool <= 0)
        {
            // Keep the water-mark current even when there's nothing to distribute.
            if (account.AmountPaidKobo != schedule.AttributedUpToKobo)
            {
                schedule.RecordAttribution(account.AmountPaidKobo, schedule.CarryCreditKobo);
                return true;
            }
            return false;
        }

        var periods = await db.BillingPeriods.IgnoreQueryFilters()
            .Where(p => p.BillingScheduleId == schedule.Id && p.Status != BillingPeriodStatus.Paid)
            .OrderBy(p => p.Sequence).ToListAsync(ct);

        var now = clock.UtcNow;
        var justPaid = new List<BillingPeriod>();
        foreach (var period in periods)
        {
            if (pool <= 0) break;
            var consumed = period.Attribute(pool, now, out var paid);
            pool -= consumed;
            if (paid) justPaid.Add(period);
        }

        schedule.RecordAttribution(account.AmountPaidKobo, pool);

        foreach (var period in justPaid)
            await outbound.PublishEventAsync(schedule.TenantId, "billing.period.paid", PeriodEventData(schedule, account, period), ct);

        return true;
    }

    // ---- Dunning worker surface (runs without a tenant context; ignores query filters) ----

    /// <summary>Open the next period for any active schedule whose current period has elapsed, then
    /// draw carried-over credit into it. Returns the number of periods opened.</summary>
    public async Task<int> OpenDuePeriodsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // Oldest-due first so every schedule makes forward progress even when more than the cap are
        // behind at once (otherwise an unordered Take could keep returning the same subset).
        var due = await db.BillingSchedules.IgnoreQueryFilters()
            .Where(s => s.Status == BillingScheduleStatus.Active
                && (s.CurrentPeriodEndUtc == null || s.CurrentPeriodEndUtc <= now))
            .OrderBy(s => s.CurrentPeriodEndUtc)
            .Take(200).ToListAsync(ct);

        var opened = 0;
        foreach (var schedule in due)
        {
            var account = await db.VirtualAccounts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(v => v.Id == schedule.VirtualAccountId, ct);
            if (account is null || account.Status != VirtualAccountStatus.Active)
                continue;

            try
            {
                var period = schedule.OpenNextPeriod(now);
                db.BillingPeriods.Add(period);
                await db.SaveChangesAsync(ct);
                await AttributeInternalAsync(schedule, account, ct); // apply carry + any DVA balance
                await db.SaveChangesAsync(ct);
                opened++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The reconciliation path advanced this schedule first; skip — the next tick re-evaluates.
            }
        }
        return opened;
    }

    /// <summary>Send the "payment due" reminder for freshly-opened, unpaid periods (once each) and
    /// emit <c>billing.period.due</c>. Returns the number notified.</summary>
    public async Task<int> SendDueRemindersAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var periods = await db.BillingPeriods.IgnoreQueryFilters()
            .Where(p => !p.DueNotified
                && (p.Status == BillingPeriodStatus.Open || p.Status == BillingPeriodStatus.PartiallyPaid))
            .OrderBy(p => p.DueDateUtc).Take(200).ToListAsync(ct);

        var count = 0;
        foreach (var period in periods)
        {
            var ctx = await LoadContextAsync(period, ct);
            if (ctx is null) continue;
            var (schedule, account, customerEmail, brand) = ctx.Value;

            if (!string.IsNullOrWhiteSpace(customerEmail))
                await email.SendBillingReminderAsync(customerEmail!, brand, period.OutstandingKobo, period.DueDateUtc,
                    account.AccountNumber, account.BankName, overdue: false, ct);
            await outbound.PublishEventAsync(schedule.TenantId, "billing.period.due", PeriodEventData(schedule, account, period), ct);

            period.MarkDueNotified();
            await db.SaveChangesAsync(ct);
            count++;
        }
        return count;
    }

    /// <summary>Flag periods past their overdue line, notify the payer once, and emit
    /// <c>billing.period.overdue</c>. Returns the number marked overdue.</summary>
    public async Task<int> MarkOverdueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // Push the overdue-line predicate into SQL and order oldest-first, so the cap bounds
        // *actually-overdue* rows — not the whole not-yet-due open population (which would starve the
        // genuinely-overdue ones behind an arbitrary Take).
        var candidates = await db.BillingPeriods.IgnoreQueryFilters()
            .Where(p => !p.OverdueNotified
                && (p.Status == BillingPeriodStatus.Open || p.Status == BillingPeriodStatus.PartiallyPaid)
                && (p.DueDateUtc > p.PeriodStartUtc ? p.DueDateUtc : p.PeriodEndUtc) < now)
            .OrderBy(p => p.DueDateUtc)
            .Take(500).ToListAsync(ct);

        var count = 0;
        foreach (var period in candidates)
        {
            if (!period.MarkOverdue()) continue;
            var ctx = await LoadContextAsync(period, ct);
            if (ctx is null) { period.MarkOverdueNotified(); await db.SaveChangesAsync(ct); continue; }
            var (schedule, account, customerEmail, brand) = ctx.Value;

            if (!string.IsNullOrWhiteSpace(customerEmail))
                await email.SendBillingReminderAsync(customerEmail!, brand, period.OutstandingKobo, period.DueDateUtc,
                    account.AccountNumber, account.BankName, overdue: true, ct);
            await outbound.PublishEventAsync(schedule.TenantId, "billing.period.overdue", PeriodEventData(schedule, account, period), ct);

            period.MarkOverdueNotified();
            await db.SaveChangesAsync(ct);
            count++;
        }
        return count;
    }

    /// <summary>Load the schedule, DVA, payer email, and merchant brand for a period. Null if the
    /// schedule or account no longer exists. Runs filter-free (worker context).</summary>
    private async Task<(BillingSchedule Schedule, VirtualAccount Account, string? CustomerEmail, string Brand)?> LoadContextAsync(
        BillingPeriod period, CancellationToken ct)
    {
        var schedule = await db.BillingSchedules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == period.BillingScheduleId, ct);
        if (schedule is null) return null;
        var account = await db.VirtualAccounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == schedule.VirtualAccountId, ct);
        if (account is null) return null;

        var customerEmail = await db.Customers.IgnoreQueryFilters()
            .Where(c => c.Id == account.CustomerId).Select(c => c.Email).FirstOrDefaultAsync(ct);
        var tenant = await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == schedule.TenantId).Select(t => new { t.Name, t.BrandName }).FirstOrDefaultAsync(ct);
        var brand = tenant is null ? account.AccountName
            : (string.IsNullOrWhiteSpace(tenant.BrandName) ? tenant.Name : tenant.BrandName!);

        return (schedule, account, customerEmail, brand);
    }

    internal static object PeriodEventData(BillingSchedule schedule, VirtualAccount account, BillingPeriod period) => new
    {
        scheduleRef = schedule.Reference,
        accountRef = account.Reference,
        accountNumber = account.AccountNumber,
        sequence = period.Sequence,
        status = period.Status.ToString(),
        expectedAmountKobo = period.ExpectedAmountKobo,
        amountAttributedKobo = period.AmountAttributedKobo,
        outstandingKobo = period.OutstandingKobo,
        periodStartUtc = period.PeriodStartUtc,
        periodEndUtc = period.PeriodEndUtc,
        dueDateUtc = period.DueDateUtc,
    };
}
