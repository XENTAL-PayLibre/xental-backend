using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Billing;
using Xental.Application.Webhooks;
using Xental.Domain.Billing;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class BillingTests
{
    private static BillingService MakeBilling(XentalDbContext ctx, TestDatabase db, FakeEmailSender? email = null) =>
        new(ctx, db.Tenant, new OutboundEventPublisher(ctx, db.Clock), email ?? new FakeEmailSender(), new FakeAlerter(), db.Clock);

    /// <summary>Seed a tenant + customer + reusable (open) DVA and make it the current tenant.</summary>
    private static async Task<Guid> SeedAsync(XentalDbContext ctx, TestDatabase db)
    {
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "cust-1", "Payer", "payer@x.com");
        ctx.Customers.Add(c);
        ctx.VirtualAccounts.Add(new VirtualAccount(t.Id, c.Id, "acct-1", "1234567890", "Bank", "Payer"));
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;
        return t.Id;
    }

    private static Task<VirtualAccount> AccountAsync(XentalDbContext ctx) =>
        ctx.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Reference == "acct-1");

    [Fact]
    public async Task Creating_a_schedule_opens_the_first_period_for_the_amount()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedAsync(ctx, db);
        var billing = MakeBilling(ctx, db);

        var view = await billing.CreateAsync("acct-1", BillingInterval.Monthly, 500_00);

        view.AccountRef.Should().Be("acct-1");
        var periods = await ctx.BillingPeriods.IgnoreQueryFilters().ToListAsync();
        periods.Should().ContainSingle();
        periods[0].ExpectedAmountKobo.Should().Be(500_00);
        periods[0].Status.Should().Be(BillingPeriodStatus.Open);
    }

    [Fact]
    public async Task A_deposit_into_the_dva_is_attributed_and_pays_the_open_period()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedAsync(ctx, db);
        var billing = MakeBilling(ctx, db);
        await billing.CreateAsync("acct-1", BillingInterval.Monthly, 500_00);

        var account = await AccountAsync(ctx);
        account.ApplyInflow(Money.FromKobo(500_00));
        await ctx.SaveChangesAsync();

        await billing.AttributeDepositAsync(account.Id);

        var period = await ctx.BillingPeriods.IgnoreQueryFilters().OrderBy(p => p.Sequence).FirstAsync();
        period.Status.Should().Be(BillingPeriodStatus.Paid);
        period.AmountAttributedKobo.Should().Be(500_00);
        period.PaidAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Overpayment_carries_forward_to_the_next_period()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedAsync(ctx, db);
        var billing = MakeBilling(ctx, db);
        await billing.CreateAsync("acct-1", BillingInterval.Monthly, 500_00);

        // Pay 800 into a 500 period: 500 settles it, 300 is carried.
        var account = await AccountAsync(ctx);
        account.ApplyInflow(Money.FromKobo(800_00));
        await ctx.SaveChangesAsync();
        await billing.AttributeDepositAsync(account.Id);

        var schedule = await ctx.BillingSchedules.IgnoreQueryFilters().FirstAsync();
        schedule.CarryCreditKobo.Should().Be(300_00);

        // Next cycle opens; the carried 300 is applied to the new 500 period, leaving it partially paid.
        db.Clock.UtcNow = db.Clock.UtcNow.AddDays(40);
        var opened = await billing.OpenDuePeriodsAsync(db.Clock.UtcNow);
        opened.Should().Be(1);

        var period2 = await ctx.BillingPeriods.IgnoreQueryFilters().OrderBy(p => p.Sequence).LastAsync();
        period2.Sequence.Should().Be(2);
        period2.AmountAttributedKobo.Should().Be(300_00);
        period2.Status.Should().Be(BillingPeriodStatus.PartiallyPaid);
    }

    [Fact]
    public async Task Next_amount_is_variable_per_cycle()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedAsync(ctx, db);
        var billing = MakeBilling(ctx, db);
        var view = await billing.CreateAsync("acct-1", BillingInterval.Monthly, 500_00);

        await billing.SetNextAmountAsync(view.Schedule.Id, 1_000_00);

        db.Clock.UtcNow = db.Clock.UtcNow.AddDays(40);
        await billing.OpenDuePeriodsAsync(db.Clock.UtcNow);

        var period2 = await ctx.BillingPeriods.IgnoreQueryFilters().OrderBy(p => p.Sequence).LastAsync();
        period2.ExpectedAmountKobo.Should().Be(1_000_00);
    }

    [Fact]
    public async Task Due_reminder_then_overdue_notice_are_each_sent_once()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedAsync(ctx, db);
        var email = new FakeEmailSender();
        var billing = MakeBilling(ctx, db, email);
        await billing.CreateAsync("acct-1", BillingInterval.Monthly, 500_00);

        // A freshly-opened, unpaid period earns a "due" reminder — once.
        (await billing.SendDueRemindersAsync(db.Clock.UtcNow)).Should().Be(1);
        (await billing.SendDueRemindersAsync(db.Clock.UtcNow)).Should().Be(0);
        email.BillingReminders.Should().Be(1);
        email.LastReminderOverdue.Should().BeFalse();

        // Once the cycle elapses unpaid, it tips into overdue and the payer is notified.
        db.Clock.UtcNow = db.Clock.UtcNow.AddDays(40);
        (await billing.MarkOverdueAsync(db.Clock.UtcNow)).Should().Be(1);
        (await billing.MarkOverdueAsync(db.Clock.UtcNow)).Should().Be(0);
        email.LastReminderOverdue.Should().BeTrue();

        var period = await ctx.BillingPeriods.IgnoreQueryFilters().FirstAsync();
        period.Status.Should().Be(BillingPeriodStatus.Overdue);
    }

    [Fact]
    public async Task A_paid_period_never_goes_overdue()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedAsync(ctx, db);
        var billing = MakeBilling(ctx, db);
        await billing.CreateAsync("acct-1", BillingInterval.Monthly, 500_00);

        var account = await AccountAsync(ctx);
        account.ApplyInflow(Money.FromKobo(500_00));
        await ctx.SaveChangesAsync();
        await billing.AttributeDepositAsync(account.Id);

        db.Clock.UtcNow = db.Clock.UtcNow.AddDays(400);
        (await billing.MarkOverdueAsync(db.Clock.UtcNow)).Should().Be(0);

        var period = await ctx.BillingPeriods.IgnoreQueryFilters().OrderBy(p => p.Sequence).FirstAsync();
        period.Status.Should().Be(BillingPeriodStatus.Paid);
    }
}
