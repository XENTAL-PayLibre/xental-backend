using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Payments;
using Xental.Domain.Billing;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class RefundTests
{
    private static RefundService Make(XentalDbContext ctx, TestDatabase db, FakeNombaClient? nomba = null, FakePayoutSwitch? sw = null) =>
        new(ctx, db.Tenant, nomba ?? new FakeNombaClient(), sw ?? new FakePayoutSwitch(), db.Clock);

    /// <summary>Seed a fixed-amount DVA (expects 500) that received an 800 deposit → 300 overpayment.</summary>
    private static async Task<(Guid tenantId, Guid vaId, string txnRef)> SeedOverpaidAsync(XentalDbContext ctx, TestDatabase db)
    {
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "cust-1", "Payer", "payer@x.com");
        ctx.Customers.Add(c);
        var va = new VirtualAccount(t.Id, c.Id, "acct-1", "1234567890", "Bank", "Payer", expectedAmountKobo: 500_00);
        va.ApplyInflow(Money.FromKobo(800_00)); // Overpaid: credit 300 over expected
        ctx.VirtualAccounts.Add(va);
        var txn = new Transaction(t.Id, va.Id, "dep-1", "Payer", Money.FromKobo(800_00), Money.Zero,
            TransactionStatus.Success, ReconciliationStatus.Overpaid, TransactionFlag.Overpaid,
            db.Clock.UtcNow, db.Clock.UtcNow, 0, senderAccountNumber: "3107560763", senderBankCode: "011");
        ctx.Transactions.Add(txn);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;
        return (t.Id, va.Id, "dep-1");
    }

    [Fact]
    public async Task Refund_sends_the_surplus_to_the_payer_and_records_a_refund_transfer()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (_, vaId, txnRef) = await SeedOverpaidAsync(ctx, db);

        var result = await Make(ctx, db).RefundOverpaymentAsync(txnRef, null);

        result.Status.Should().Be("refunded");
        result.AmountKobo.Should().Be(300_00, "only the overpayment surplus is returned");
        result.DestinationAccountNumber.Should().Be("3107560763", "the payer's captured source account");

        var transfer = await ctx.Transfers.IgnoreQueryFilters().SingleAsync(t => t.MerchantTxRef.StartsWith($"refund-{vaId:N}-"));
        transfer.Status.Should().Be(TransferStatus.Success);
        transfer.AmountKobo.Should().Be(300_00);
    }

    [Fact]
    public async Task Refund_releases_an_active_overpayment_hold()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (tenantId, vaId, txnRef) = await SeedOverpaidAsync(ctx, db);
        ctx.EscrowHolds.Add(new EscrowHold(tenantId, vaId, 800_00, "money-rule:Overpaid"));
        await ctx.SaveChangesAsync();

        await Make(ctx, db).RefundOverpaymentAsync(txnRef, null);

        var hold = await ctx.EscrowHolds.IgnoreQueryFilters().SingleAsync(e => e.VirtualAccountId == vaId);
        hold.State.Should().Be(EscrowState.Released);
    }

    [Fact]
    public async Task Refund_is_idempotent_per_deposit()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (_, vaId, txnRef) = await SeedOverpaidAsync(ctx, db);

        (await Make(ctx, db).RefundOverpaymentAsync(txnRef, null)).Status.Should().Be("refunded");
        (await Make(ctx, db).RefundOverpaymentAsync(txnRef, null)).Status.Should().Be("already_refunded");

        (await ctx.Transfers.IgnoreQueryFilters().CountAsync(t => t.MerchantTxRef.StartsWith($"refund-{vaId:N}-")))
            .Should().Be(1, "a second click never sends a second payout");
    }

    [Fact]
    public async Task Refund_is_blocked_by_the_payout_kill_switch()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (_, _, txnRef) = await SeedOverpaidAsync(ctx, db);

        var svc = Make(ctx, db, sw: new FakePayoutSwitch { PayoutsEnabled = false });
        var act = () => svc.RefundOverpaymentAsync(txnRef, null);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Refund_requires_a_destination_when_the_payer_account_was_not_captured()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        // Seed an overpayment without a captured sender account.
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "cust-1", "Payer");
        ctx.Customers.Add(c);
        var va = new VirtualAccount(t.Id, c.Id, "acct-1", "1234567890", "Bank", "Payer", expectedAmountKobo: 500_00);
        va.ApplyInflow(Money.FromKobo(800_00));
        ctx.VirtualAccounts.Add(va);
        ctx.Transactions.Add(new Transaction(t.Id, va.Id, "dep-1", "Payer", Money.FromKobo(800_00), Money.Zero,
            TransactionStatus.Success, ReconciliationStatus.Overpaid, TransactionFlag.Overpaid, db.Clock.UtcNow, db.Clock.UtcNow));
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;

        // No stored account + no override → rejected.
        var act = () => Make(ctx, db).RefundOverpaymentAsync("dep-1", null);
        await act.Should().ThrowAsync<ValidationException>();

        // Supplying a destination succeeds.
        var result = await Make(ctx, db).RefundOverpaymentAsync("dep-1", new RefundDestination("0011223344", "058", "Payer"));
        result.Status.Should().Be("refunded");
        result.DestinationAccountNumber.Should().Be("0011223344");
    }

    [Fact]
    public async Task Refund_on_a_billing_account_draws_from_carry_credit()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "cust-1", "Payer", "payer@x.com");
        ctx.Customers.Add(c);
        var va = new VirtualAccount(t.Id, c.Id, "acct-1", "1234567890", "Bank", "Payer"); // reusable/open
        va.ApplyInflow(Money.FromKobo(800_00));
        ctx.VirtualAccounts.Add(va);
        ctx.Transactions.Add(new Transaction(t.Id, va.Id, "dep-1", "Payer", Money.FromKobo(800_00), Money.Zero,
            TransactionStatus.Success, ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow,
            0, "3107560763", "011"));
        var schedule = new BillingSchedule(t.Id, va.Id, c.Id, "bsch-1", BillingInterval.Monthly, 500_00);
        // Simulate attribution having filled a 500 period and carried 300 forward.
        schedule.RecordAttribution(800_00, 300_00);
        ctx.BillingSchedules.Add(schedule);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;

        var result = await Make(ctx, db).RefundOverpaymentAsync("dep-1", null);
        result.AmountKobo.Should().Be(300_00, "the carried-forward surplus, not the whole balance");

        var after = await ctx.BillingSchedules.IgnoreQueryFilters().SingleAsync();
        after.CarryCreditKobo.Should().Be(0, "the refunded surplus no longer pre-pays the next cycle");
    }
}
