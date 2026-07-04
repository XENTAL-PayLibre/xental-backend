using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xental.Application.Payments;
using Xental.Application.Webhooks;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Domain.Webhooks;
using Xental.Infrastructure.Security;
using Xental.Infrastructure.Webhooks;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class SecretProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_round_trips_and_ciphertext_hides_plaintext()
    {
        var p = new AesSecretProtector(Options.Create(new JwtOptions { SigningKey = new string('k', 40) }));
        var secret = "whsec_super-secret-value";
        var cipher = p.Protect(secret);
        cipher.Should().NotContain("whsec");
        p.Unprotect(cipher).Should().Be(secret);
    }
}

public class OutboundUrlGuardTests
{
    private readonly OutboundUrlGuard _guard = new();

    [Theory]
    [InlineData("http://example.com/hook")]     // not https
    [InlineData("https://localhost/hook")]      // loopback
    [InlineData("https://127.0.0.1/hook")]      // loopback
    [InlineData("https://10.1.2.3/hook")]       // private
    [InlineData("https://169.254.1.1/hook")]    // link-local
    [InlineData("not-a-url")]
    public async Task Rejects_unsafe_urls(string url) => (await _guard.IsSafeAsync(url)).Should().BeFalse();

    [Fact]
    public async Task Accepts_a_public_https_endpoint() =>
        (await _guard.IsSafeAsync("https://8.8.8.8/hook")).Should().BeTrue();
}

public class WebhookDeliveryTests
{
    [Fact]
    public void Failures_back_off_then_dead_letter_at_the_cap()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var d = new WebhookDelivery(Guid.NewGuid(), Guid.NewGuid(), "e1", "deposit.reconciled", "{}", now);
        for (var i = 0; i < 7; i++)
        {
            d.RecordFailure("boom", 500, now, maxAttempts: 8);
            d.Status.Should().Be(WebhookDeliveryStatus.Failed);
            d.NextAttemptAtUtc.Should().NotBeNull();
        }
        d.RecordFailure("boom", 500, now, maxAttempts: 8); // 8th
        d.Status.Should().Be(WebhookDeliveryStatus.DeadLetter);
        d.NextAttemptAtUtc.Should().BeNull();

        d.Replay(now);
        d.Status.Should().Be(WebhookDeliveryStatus.Pending);
        d.Attempts.Should().Be(0);
    }
}

public class OutboundEmitTests
{
    private static NombaWebhookService Service(TestDatabase db, Xental.Infrastructure.Persistence.XentalDbContext ctx) =>
        new(ctx, new FakeSignatureVerifier(true), new RiskEvaluator(ctx, db.Clock), new OutboundEventPublisher(ctx, db.Clock),
            new Xental.Infrastructure.Payments.InMemoryReconciliationNotifier(),
            new RuleEngine(ctx, new OutboundEventPublisher(ctx, db.Clock), db.Clock),
            new Xental.Application.Billing.BillingService(ctx, db.Tenant, new OutboundEventPublisher(ctx, db.Clock), new FakeEmailSender(), db.Clock),
            db.Clock);

    [Fact]
    public async Task Reconciled_deposit_queues_a_delivery_for_each_active_endpoint()
    {
        using var db = new TestDatabase();
        await using (var ctx = db.CreateContext())
        {
            var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
            ctx.Tenants.Add(t);
            var c = new Customer(t.Id, "ref-1", "Payer");
            ctx.Customers.Add(c);
            ctx.VirtualAccounts.Add(new VirtualAccount(t.Id, c.Id, "ref-1", "1234567890", "Bank", "Name", expectedAmountKobo: 500_00));
            ctx.WebhookEndpoints.Add(new WebhookEndpoint(t.Id, "https://hook.example.com/x", "enc"));
            await ctx.SaveChangesAsync();
        }

        var body = Encoding.UTF8.GetBytes("""
            {"event_type":"payment_success","data":{"transaction":{"transactionId":"txn-1","responseCode":"00","aliasAccountNumber":"1234567890","transactionAmount":"500.00","time":"2026-07-02T10:00:00Z"}}}
            """);
        await using (var ctx = db.CreateContext())
            await Service(db, ctx).ProcessAsync(body, "sig", "ts");

        await using var check = db.CreateContext();
        var deliveries = await check.WebhookDeliveries.IgnoreQueryFilters().ToListAsync();
        deliveries.Should().ContainSingle();
        deliveries[0].EventType.Should().Be("deposit.reconciled");
        deliveries[0].Status.Should().Be(WebhookDeliveryStatus.Pending);
    }
}

public class RiskAndTransferTests
{
    [Fact]
    public async Task Name_reused_across_accounts_raises_risk()
    {
        using var db = new TestDatabase();
        Guid tenantId, accountId;
        await using (var ctx = db.CreateContext())
        {
            var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
            var c = new Customer(t.Id, "ref-1", "Payer"); ctx.Customers.Add(c);
            var va = new VirtualAccount(t.Id, c.Id, "ref-1", "1234567890", "Bank", "Name", expectedAmountKobo: 500_00);
            var vb = new VirtualAccount(t.Id, c.Id, "ref-2", "2222222222", "Bank", "Name", expectedAmountKobo: 500_00);
            ctx.VirtualAccounts.AddRange(va, vb);
            // Same payer name hitting two different accounts within 24h.
            ctx.Transactions.Add(new Transaction(t.Id, va.Id, "x1", "Mule Co", Xental.Domain.Common.Money.FromKobo(100), Xental.Domain.Common.Money.Zero, TransactionStatus.Success, ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
            ctx.Transactions.Add(new Transaction(t.Id, vb.Id, "x2", "Mule Co", Xental.Domain.Common.Money.FromKobo(100), Xental.Domain.Common.Money.Zero, TransactionStatus.Success, ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
            await ctx.SaveChangesAsync();
            tenantId = t.Id; accountId = va.Id;
        }

        await using var ctx2 = db.CreateContext();
        var account = await ctx2.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId);
        var inflow = new NombaInflow("x3", "1234567890", 100, 0, "Mule Co", "payment_success", db.Clock.UtcNow);
        var score = await new RiskEvaluator(ctx2, db.Clock).ScoreAsync(account, inflow, nameMismatch: false);
        score.Should().BeGreaterThanOrEqualTo(40, "name reused across 2+ accounts is a mule signal");
    }

    [Fact]
    public async Task Transfer_is_idempotent_on_merchant_ref()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;

        var svc = new TransferService(ctx, db.Tenant, new FakeNombaClient(),
            Microsoft.Extensions.Options.Options.Create(new Xental.Application.Common.TierLimitOptions()), db.Clock);
        var a = await svc.InitiateAsync("mtx-1", 100_00, "0123456789", "011", null);
        var b = await svc.InitiateAsync("mtx-1", 100_00, "0123456789", "011", null);
        b.Id.Should().Be(a.Id, "same merchantTxRef must not move money twice");
        a.Status.Should().Be(TransferStatus.Success);

        (await ctx.Transfers.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Daily_payout_cap_blocks_transfers_over_the_limit()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;

        var limits = Microsoft.Extensions.Options.Options.Create(
            new Xental.Application.Common.TierLimitOptions { DailyPayoutCapKobo = 10_000 });
        var svc = new TransferService(ctx, db.Tenant, new FakeNombaClient(), limits, db.Clock);

        var ok = await svc.InitiateAsync("mtx-1", 6_000, "0123456789", "011", null);
        ok.Status.Should().Be(TransferStatus.Success);

        var overCap = () => svc.InitiateAsync("mtx-2", 6_000, "0123456789", "011", null);
        await overCap.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();
    }
}
