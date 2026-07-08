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
        var svc = new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient("9011223344"));
        var va = await svc.CreateAsync("stu-001", "Ada Payer", "ada@x.com", null, 500_00, null, null, testMode: false);

        va.AccountNumber.Should().Be("9011223344");
        va.Reference.Should().Be("stu-001");
        va.ExpectedAmountKobo.Should().Be(500_00);

        var reloaded = await new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient()).GetByReferenceAsync("stu-001");
        reloaded.AccountNumber.Should().Be("9011223344");
    }

    [Fact]
    public async Task Test_mode_provisions_a_simulated_sandbox_nuban_not_the_provider()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        await using var ctx = db.CreateContext();
        var svc = new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient("1234567890"));

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
            await new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient()).CreateAsync("dup", "A", null, null, null, null, null, testMode: false);

        await using var ctx2 = db.CreateContext();
        var act = () => new VirtualAccountService(ctx2, db.Tenant, new FakeNombaClient()).CreateAsync("dup", "B", null, null, null, null, null, testMode: false);
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Reference_is_generated_when_omitted()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        await using var ctx = db.CreateContext();

        var va = await new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient())
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

        var act = () => new VirtualAccountService(ctx, db.Tenant, new FakeNombaClient())
            .CreateAsync("ref-1", "Ada", null, null, null, null, null, testMode: true);
        await act.Should().ThrowAsync<EmailNotVerifiedException>();
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
}
