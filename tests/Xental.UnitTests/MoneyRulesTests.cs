using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Payments;
using Xental.Application.Webhooks;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Domain.Webhooks;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class MoneyRulesTests
{
    private static RuleEngine Engine(TestDatabase db, XentalDbContext ctx) =>
        new(ctx, new OutboundEventPublisher(ctx, db.Clock), db.Clock);

    /// <summary>Seed a tenant + an overpaid account (expected 1000, paid 1200) and its deposit txn.</summary>
    private static async Task<(Guid tenantId, VirtualAccount account, Transaction txn)> SeedOverpaidAsync(TestDatabase db, XentalDbContext ctx, int riskScore = 0)
    {
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "ref-1", "Payer"); ctx.Customers.Add(c);
        var va = new VirtualAccount(t.Id, c.Id, "ref-1", "1234567890", "Bank", "Payer", expectedAmountKobo: 1_000_00);
        va.ApplyInflow(Money.FromKobo(1_200_00)); // -> Overpaid, credit 200.00
        ctx.VirtualAccounts.Add(va);
        var txn = new Transaction(t.Id, va.Id, "x1", "Payer", Money.FromKobo(1_200_00), Money.FromKobo(0),
            TransactionStatus.Success, ReconciliationStatus.Overpaid, TransactionFlag.Overpaid, db.Clock.UtcNow, db.Clock.UtcNow, riskScore);
        ctx.Transactions.Add(txn);
        await ctx.SaveChangesAsync();
        return (t.Id, va, txn);
    }

    [Fact]
    public async Task No_rules_is_a_noop()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (_, va, txn) = await SeedOverpaidAsync(db, ctx);
        await Engine(db, ctx).EvaluateAsync(va, txn);
        (await ctx.EscrowHolds.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Overpaid_hold_rule_places_an_escrow_hold()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (tid, va, txn) = await SeedOverpaidAsync(db, ctx);
        ctx.MoneyRules.Add(new MoneyRule(tid, RuleTrigger.Overpaid, RuleAction.Hold, thresholdKobo: 100_00, minRiskScore: null, priority: 0));
        await ctx.SaveChangesAsync();

        await Engine(db, ctx).EvaluateAsync(va, txn);

        var hold = await ctx.EscrowHolds.IgnoreQueryFilters().SingleAsync();
        hold.VirtualAccountId.Should().Be(va.Id);
        hold.State.Should().Be(EscrowState.Held);
    }

    [Fact]
    public async Task Rule_below_threshold_does_not_fire()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (tid, va, txn) = await SeedOverpaidAsync(db, ctx);
        // Overpayment is 200.00; threshold 500.00 -> no fire.
        ctx.MoneyRules.Add(new MoneyRule(tid, RuleTrigger.Overpaid, RuleAction.Hold, thresholdKobo: 500_00, minRiskScore: null, priority: 0));
        await ctx.SaveChangesAsync();

        await Engine(db, ctx).EvaluateAsync(va, txn);

        (await ctx.EscrowHolds.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hold_is_idempotent_across_repeated_evaluation()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (tid, va, txn) = await SeedOverpaidAsync(db, ctx);
        ctx.MoneyRules.Add(new MoneyRule(tid, RuleTrigger.Overpaid, RuleAction.Hold, 0, null, 0));
        await ctx.SaveChangesAsync();

        await Engine(db, ctx).EvaluateAsync(va, txn);
        await Engine(db, ctx).EvaluateAsync(va, txn);

        (await ctx.EscrowHolds.IgnoreQueryFilters().CountAsync()).Should().Be(1, "only one active hold per account");
    }

    [Fact]
    public async Task Notify_rule_queues_a_webhook_delivery_for_active_endpoints()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var (tid, va, txn) = await SeedOverpaidAsync(db, ctx, riskScore: 90);
        ctx.WebhookEndpoints.Add(new WebhookEndpoint(tid, "https://hook.example.com/x", "whsec_test"));
        ctx.MoneyRules.Add(new MoneyRule(tid, RuleTrigger.HighRisk, RuleAction.Notify, null, minRiskScore: 50, priority: 0));
        await ctx.SaveChangesAsync();

        await Engine(db, ctx).EvaluateAsync(va, txn);

        var deliveries = await ctx.WebhookDeliveries.IgnoreQueryFilters().ToListAsync();
        deliveries.Should().ContainSingle();
        deliveries[0].EventType.Should().Be("rule.notify");
    }
}
