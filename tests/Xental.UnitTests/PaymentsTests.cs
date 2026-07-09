using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Exceptions;
using Xental.Application.Payments;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Infrastructure.Nomba;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class NombaSignatureVerifierTests
{
    private const string Secret = "NombaHackathon2026";
    private const string Body = """
        {"event_type":"payment_success","requestId":"req-1",
         "data":{"merchant":{"walletId":"w-1","userId":"u-1"},
         "transaction":{"transactionId":"t-1","type":"vact_transfer","time":"2026-07-02T10:51:44Z","responseCode":""}}}
        """;
    private const string Ts = "2026-07-02T10:51:44Z";

    private static string Sign(string secret, string body, string ts)
    {
        // Independent recompute of Nomba's colon-delimited 9-field payload.
        var payload = $"payment_success:req-1:u-1:w-1:t-1:vact_transfer:2026-07-02T10:51:44Z::{ts}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static NombaSignatureVerifier Verifier() =>
        new(Options.Create(new NombaOptions { WebhookSecret = Secret, WebhookMaxAgeMinutes = 0 }),
            new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

    [Fact]
    public void Accepts_a_correctly_signed_payload()
    {
        var sig = Sign(Secret, Body, Ts);
        Verifier().Verify(Encoding.UTF8.GetBytes(Body), sig, Ts).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_tampered_signature()
    {
        Verifier().Verify(Encoding.UTF8.GetBytes(Body), Sign("wrong-secret", Body, Ts), Ts).Should().BeFalse();
    }

    [Fact]
    public void Rejects_when_no_signature_header()
    {
        Verifier().Verify(Encoding.UTF8.GetBytes(Body), null, Ts).Should().BeFalse();
    }
}

public class VirtualAccountReconciliationTests
{
    private static VirtualAccount Account(long? expectedKobo) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "ref-1", "1234567890", "Bank", "Name", expectedAmountKobo: expectedKobo);

    [Fact]
    public void Exact_payment_marks_fully_paid()
    {
        var a = Account(500_00);
        a.ApplyInflow(Money.FromKobo(500_00)).Should().Be(ReconciliationStatus.Reconciled);
        a.PaymentState.Should().Be(PaymentState.FullyPaid);
        a.Deficit.Kobo.Should().Be(0);
    }

    [Fact]
    public void Short_then_topup_accumulates_to_fully_paid()
    {
        var a = Account(500_00);
        a.ApplyInflow(Money.FromKobo(300_00)).Should().Be(ReconciliationStatus.Underpaid);
        a.PaymentState.Should().Be(PaymentState.PartiallyPaid);
        a.Deficit.Kobo.Should().Be(200_00);
        a.ApplyInflow(Money.FromKobo(200_00)).Should().Be(ReconciliationStatus.Reconciled);
        a.PaymentState.Should().Be(PaymentState.FullyPaid);
    }

    [Fact]
    public void Overpayment_records_rolling_credit()
    {
        var a = Account(500_00);
        a.ApplyInflow(Money.FromKobo(600_00)).Should().Be(ReconciliationStatus.Overpaid);
        a.PaymentState.Should().Be(PaymentState.Overpaid);
        a.OverpaymentCredit.Kobo.Should().Be(100_00);
    }

    [Fact]
    public void No_expectation_is_credited_as_reconciled()
    {
        var a = Account(null);
        a.ApplyInflow(Money.FromKobo(100_00)).Should().Be(ReconciliationStatus.Reconciled);
        a.PaymentState.Should().Be(PaymentState.Unpaid);
        a.AmountPaidKobo.Should().Be(100_00);
    }

    [Fact]
    public void Reversal_backs_out_the_credit()
    {
        var a = Account(500_00);
        a.ApplyInflow(Money.FromKobo(500_00));
        a.ReverseInflow(Money.FromKobo(500_00));
        a.AmountPaidKobo.Should().Be(0);
        a.PaymentState.Should().Be(PaymentState.Unpaid);
    }
}

public class VirtualAccountServiceTests
{
    private static async Task<Guid> SeedTenantAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var t = new Xental.Domain.Tenancy.Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        t.MarkEmailVerified(); // provisioning requires a verified account
        ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    [Fact]
    public async Task Create_provisions_and_persists_a_nuban()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);

        await using var ctx = db.CreateContext();
        var svc = new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient("9011223344"), new FakeEmailSender());
        var va = await svc.CreateAsync("stu-001", "Ada Payer", "ada@x.com", null, 500_00, null, null, testMode: false);

        va.AccountNumber.Should().Be("9011223344");
        va.Reference.Should().Be("stu-001");
        va.ExpectedAmountKobo.Should().Be(500_00);

        var reloaded = await new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient(), new FakeEmailSender()).GetByReferenceAsync("stu-001");
        reloaded.Account.AccountNumber.Should().Be("9011223344");
        reloaded.CustomerName.Should().Be("Ada Payer");
        reloaded.CustomerEmail.Should().Be("ada@x.com");
    }

    [Fact]
    public async Task Delete_removes_an_account_with_no_activity_but_refuses_one_with_payments()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        await using var ctx = db.CreateContext();
        var svc = new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient("9011223344"), new FakeEmailSender());

        await svc.CreateAsync("del-ok", "No Pay", "a@x.com", null, null, null, null, testMode: true);
        await svc.CreateAsync("del-paid", "Has Pay", "b@x.com", null, null, null, null, testMode: true);

        // Simulate a payment on the second account.
        var paid = await ctx.VirtualAccounts.FirstAsync(v => v.Reference == "del-paid");
        paid.ApplyInflow(Money.FromKobo(1_000_00));
        await ctx.SaveChangesAsync();

        await svc.DeleteAsync("del-ok");
        (await ctx.VirtualAccounts.AnyAsync(v => v.Reference == "del-ok")).Should().BeFalse();

        var blocked = () => svc.DeleteAsync("del-paid");
        await blocked.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Test_mode_provisions_a_simulated_sandbox_nuban_not_the_provider()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        await using var ctx = db.CreateContext();
        var svc = new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient("1234567890"), new FakeEmailSender());

        var va = await svc.CreateAsync("test-1", "Sandbox Payer", null, null, null, null, null, testMode: true);

        va.AccountNumber.Should().StartWith("99").And.HaveLength(10);
        va.AccountNumber.Should().NotBe("1234567890", "test mode simulates instead of calling the provider");
        va.BankName.Should().Be("Xental Sandbox Bank");
    }

    [Fact]
    public async Task Duplicate_accountRef_conflicts()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        await using (var ctx = db.CreateContext())
            await new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient(), new FakeEmailSender()).CreateAsync("dup", "A", null, null, null, null, null, testMode: false);

        await using var ctx2 = db.CreateContext();
        var act = () => new VirtualAccountService(ctx2, db.Tenant, new FakeNombaClient(), new FakeEmailSender()).CreateAsync("dup", "B", null, null, null, null, null, testMode: false);
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Reference_is_generated_when_omitted()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        await using var ctx = db.CreateContext();

        var va = await new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient(), new FakeEmailSender())
            .CreateAsync(null, "Ada Payer", null, null, null, null, null, testMode: true);

        va.Reference.Should().StartWith("cust_").And.HaveLength("cust_".Length + 16);
    }

    [Fact]
    public async Task Unverified_account_cannot_create_a_customer()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var t = new Xental.Domain.Tenancy.Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); // NOT verified
        ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;

        var act = () => new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient(), new FakeEmailSender())
            .CreateAsync("ref-1", "Ada", null, null, null, null, null, testMode: true);
        await act.Should().ThrowAsync<EmailNotVerifiedException>();
    }

    [Fact]
    public async Task Summary_reports_total_successful_and_failed_payins()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        db.Tenant.TenantId = tenantId;
        await using (var ctx = db.CreateContext())
        {
            ctx.Transactions.Add(new Transaction(tenantId, null, "t1", "P", Money.FromKobo(500_00), Money.FromKobo(10_00),
                TransactionStatus.Success, ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
            ctx.Transactions.Add(new Transaction(tenantId, null, "t2", "P", Money.FromKobo(800_00), Money.Zero,
                TransactionStatus.Success, ReconciliationStatus.Overpaid, TransactionFlag.Overpaid, db.Clock.UtcNow, db.Clock.UtcNow));
            ctx.Transactions.Add(new Transaction(tenantId, null, "t3", "P", Money.FromKobo(500_00), Money.Zero,
                TransactionStatus.Failed, ReconciliationStatus.Reversed, TransactionFlag.Reversed, db.Clock.UtcNow, db.Clock.UtcNow));
            await ctx.SaveChangesAsync();
        }

        await using var check = db.CreateContext();
        var s = await new TransactionQueryService(check, db.Tenant).SummaryAsync(null, null);

        s.Total.Should().Be(3);
        s.Successful.Should().Be(2);
        s.Failed.Should().Be(1);
        s.TotalPayinsKobo.Should().Be(1300_00, "gross of non-reversed deposits (500 + 800)");
    }
}

public class NombaWebhookServiceTests
{
    private static string Payload(string txnId, string account, decimal naira) => $$"""
        { "event_type":"payment_success",
          "data": { "transaction": {
            "transactionId":"{{txnId}}", "responseCode":"00",
            "aliasAccountNumber":"{{account}}", "amount":"{{naira}}",
            "time":"2026-07-02T10:00:00Z" } } }
        """;

    private static async Task<Guid> SeedAccountAsync(TestDatabase db, string account, long? expectedKobo)
    {
        await using var ctx = db.CreateContext();
        var t = new Xental.Domain.Tenancy.Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "ref-1", "Payer");
        ctx.Customers.Add(c);
        ctx.VirtualAccounts.Add(new VirtualAccount(t.Id, c.Id, "ref-1", account, "Bank", "Name", expectedAmountKobo: expectedKobo));
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private static NombaWebhookService Service(TestDatabase db, XentalDbContext ctx, bool sigOk = true) =>
        new(ctx, new FakeSignatureVerifier(sigOk),
            new RiskEvaluator(ctx, db.Clock),
            new Xental.Application.Webhooks.OutboundEventPublisher(ctx, db.Clock),
            new Xental.Infrastructure.Payments.InMemoryReconciliationNotifier(),
            new RuleEngine(ctx, new Xental.Application.Webhooks.OutboundEventPublisher(ctx, db.Clock), db.Clock),
            new FlowEngine(ctx, new Xental.Application.Webhooks.OutboundEventPublisher(ctx, db.Clock), db.Clock),
            new Xental.Application.Billing.BillingService(ctx, db.Tenant, new Xental.Application.Webhooks.OutboundEventPublisher(ctx, db.Clock), new FakeEmailSender(), new FakeAlerter(), db.Clock),
            db.Clock);

    [Fact]
    public async Task Valid_exact_inflow_reconciles_and_writes_one_ledger_entry()
    {
        using var db = new TestDatabase();
        await SeedAccountAsync(db, "1234567890", 500_00);
        var body = Encoding.UTF8.GetBytes(Payload("txn-1", "1234567890", 500.00m));

        WebhookResult result;
        await using (var ctx = db.CreateContext())
            result = await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        result.Status.Should().Be(WebhookStatus.Processed);
        result.Reconciliation.Should().Be(ReconciliationStatus.Reconciled);
        result.PaymentState.Should().Be(PaymentState.FullyPaid);

        await using var check = db.CreateContext();
        (await check.Transactions.CountAsync()).Should().Be(1);
        (await check.VirtualAccounts.IgnoreQueryFilters().SingleAsync()).AmountPaidKobo.Should().Be(500_00);
    }

    [Fact]
    public async Task Duplicate_requestId_is_a_no_op()
    {
        using var db = new TestDatabase();
        await SeedAccountAsync(db, "1234567890", 500_00);
        var body = Encoding.UTF8.GetBytes(Payload("txn-dup", "1234567890", 500.00m));

        await using (var ctx = db.CreateContext()) await Service(db, ctx).ProcessAsync(body, "sig", "ts");
        WebhookResult second;
        await using (var ctx = db.CreateContext()) second = await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        second.Status.Should().Be(WebhookStatus.Duplicate);
        await using var check = db.CreateContext();
        (await check.Transactions.CountAsync()).Should().Be(1, "replay must not double-credit");
        (await check.VirtualAccounts.IgnoreQueryFilters().SingleAsync()).AmountPaidKobo.Should().Be(500_00);
    }

    [Fact]
    public async Task Bad_signature_is_rejected()
    {
        using var db = new TestDatabase();
        await SeedAccountAsync(db, "1234567890", 500_00);
        var body = Encoding.UTF8.GetBytes(Payload("txn-x", "1234567890", 500.00m));

        await using var ctx = db.CreateContext();
        var act = () => Service(db, ctx, sigOk: false).ProcessAsync(body, "bad", "ts");
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Unknown_account_goes_to_review_queue()
    {
        using var db = new TestDatabase();
        await SeedAccountAsync(db, "1234567890", 500_00);
        var body = Encoding.UTF8.GetBytes(Payload("txn-u", "9999999999", 500.00m));

        WebhookResult result;
        await using (var ctx = db.CreateContext()) result = await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        result.Status.Should().Be(WebhookStatus.Review);
        result.Reconciliation.Should().Be(ReconciliationStatus.PendingReview);
        await using var check = db.CreateContext();
        var review = await check.Transactions.SingleAsync();
        review.Reconciliation.Should().Be(ReconciliationStatus.PendingReview);
        review.Reason.Should().Be(TransactionFlag.InvalidAccount);
        review.VirtualAccountId.Should().BeNull();
    }

    [Fact]
    public async Task Name_mismatch_is_flagged_but_still_reconciled()
    {
        using var db = new TestDatabase();
        await SeedAccountAsync(db, "1234567890", 500_00); // customer name is "Payer"
        var body = Encoding.UTF8.GetBytes($$"""
            { "event_type":"payment_success",
              "data": { "transaction": {
                "transactionId":"txn-nm", "responseCode":"00", "aliasAccountNumber":"1234567890",
                "transactionAmount":"500.00", "senderName":"Totally Different Person",
                "time":"2026-07-02T10:00:00Z" } } }
            """);

        WebhookResult result;
        await using (var ctx = db.CreateContext()) result = await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        result.Status.Should().Be(WebhookStatus.Processed);
        result.Reconciliation.Should().Be(ReconciliationStatus.Reconciled);
        result.Reason.Should().Be(TransactionFlag.NameMismatch);
    }

    [Fact]
    public async Task Overpayment_sets_overpaid_state()
    {
        using var db = new TestDatabase();
        await SeedAccountAsync(db, "1234567890", 500_00);
        var body = Encoding.UTF8.GetBytes(Payload("txn-o", "1234567890", 600.00m));

        WebhookResult result;
        await using (var ctx = db.CreateContext()) result = await Service(db, ctx).ProcessAsync(body, "sig", "ts");
        result.Reconciliation.Should().Be(ReconciliationStatus.Overpaid);
        result.PaymentState.Should().Be(PaymentState.Overpaid);
    }

    // ---- Programmable Payment Flows ----

    private static async Task AddFlowAsync(TestDatabase db, Guid tenantId, string name, FlowTrigger trigger,
        FlowActionType[] actions, long? minAmountKobo = null, bool enabled = true)
    {
        await using var ctx = db.CreateContext();
        var flow = new Flow(tenantId, name, trigger, minAmountKobo, null, 1, db.Clock.UtcNow);
        flow.SetActions(actions);
        if (!enabled) flow.SetEnabled(false);
        ctx.Flows.Add(flow);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Flow_on_overpaid_holds_notifies_and_writes_an_audit_run()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedAccountAsync(db, "9000000001", 500_00);
        await AddFlowAsync(db, tenantId, "Hold overpayments", FlowTrigger.Overpaid,
            new[] { FlowActionType.Hold, FlowActionType.NotifyWebhook });

        var body = Encoding.UTF8.GetBytes(Payload("txn-of", "9000000001", 600.00m)); // ₦600 > ₦500 expected
        await using (var ctx = db.CreateContext()) await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        await using var check = db.CreateContext();
        (await check.EscrowHolds.IgnoreQueryFilters().CountAsync(e => e.State == EscrowState.Held)).Should().Be(1);
        var run = await check.FlowRuns.IgnoreQueryFilters().SingleAsync();
        run.FlowName.Should().Be("Hold overpayments");
        run.Trigger.Should().Be("Overpaid");
        run.Outcome.Should().Contain("Hold").And.Contain("flow.notify");
    }

    [Fact]
    public async Task Flow_does_not_run_when_the_trigger_does_not_match()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedAccountAsync(db, "9000000002", 500_00);
        await AddFlowAsync(db, tenantId, "Only underpaid", FlowTrigger.Underpaid, new[] { FlowActionType.Hold });

        var body = Encoding.UTF8.GetBytes(Payload("txn-exact", "9000000002", 500.00m)); // exact → Reconciled
        await using (var ctx = db.CreateContext()) await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        await using var check = db.CreateContext();
        (await check.FlowRuns.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await check.EscrowHolds.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Flow_min_amount_gate_skips_small_deposits()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedAccountAsync(db, "9000000003", null); // no expectation → any deposit reconciles
        await AddFlowAsync(db, tenantId, "Big deposits only", FlowTrigger.Deposit,
            new[] { FlowActionType.NotifyWebhook }, minAmountKobo: 1_000_00);

        var body = Encoding.UTF8.GetBytes(Payload("txn-small", "9000000003", 500.00m)); // ₦500 < ₦1,000 gate
        await using (var ctx = db.CreateContext()) await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        await using var check = db.CreateContext();
        (await check.FlowRuns.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Disabled_flow_never_runs()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedAccountAsync(db, "9000000004", 500_00);
        await AddFlowAsync(db, tenantId, "Off", FlowTrigger.Deposit, new[] { FlowActionType.NotifyWebhook }, enabled: false);

        var body = Encoding.UTF8.GetBytes(Payload("txn-off", "9000000004", 500.00m));
        await using (var ctx = db.CreateContext()) await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        await using var check = db.CreateContext();
        (await check.FlowRuns.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task FlowService_crud_roundtrip()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedAccountAsync(db, "9000000005", 500_00);
        db.Tenant.TenantId = tenantId;

        await using var ctx = db.CreateContext();
        var svc = new FlowService(ctx, db.Tenant, db.Clock);

        var created = await svc.CreateAsync(new FlowSpec("Auto-hold", "Overpaid", new[] { "Hold", "NotifyWebhook" }, null, null, 1));
        created.Actions.Should().HaveCount(2);
        (await svc.ListAsync()).Should().ContainSingle();

        var updated = await svc.UpdateAsync(created.Id, new FlowSpec("Auto-hold v2", "Underpaid", new[] { "ReviewFlag" }, 100_00, null, 2));
        updated.Trigger.Should().Be(FlowTrigger.Underpaid);
        updated.Actions.Should().ContainSingle();

        await svc.SetEnabledAsync(updated.Id, false);
        (await svc.GetAsync(updated.Id)).Enabled.Should().BeFalse();

        await svc.DeleteAsync(updated.Id);
        (await svc.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task FlowService_rejects_unknown_trigger_and_empty_actions()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedAccountAsync(db, "9000000006", 500_00);
        db.Tenant.TenantId = tenantId;
        await using var ctx = db.CreateContext();
        var svc = new FlowService(ctx, db.Tenant, db.Clock);

        var badTrigger = () => svc.CreateAsync(new FlowSpec("x", "Nope", new[] { "Hold" }, null, null, 1));
        await badTrigger.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();

        var noActions = () => svc.CreateAsync(new FlowSpec("x", "Deposit", Array.Empty<string>(), null, null, 1));
        await noActions.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();
    }
}
