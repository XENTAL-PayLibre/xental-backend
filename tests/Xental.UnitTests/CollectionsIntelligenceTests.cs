using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Payments;
using Xental.Domain.Billing;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class CollectionsIntelligenceTests
{
    private static InsightsService Service(TestDatabase db, XentalDbContext ctx) =>
        new(ctx, db.Tenant, db.Clock);

    private static async Task<Guid> SeedTenantAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var t = new Xental.Domain.Tenancy.Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    /// <summary>Insert an account with a deficit, then age its CreatedAtUtc (the Modified save leaves CreatedAtUtc).</summary>
    private static async Task SeedDeficitAccountAsync(TestDatabase db, Guid tenantId, Guid customerId, string reference, long expected, long paid, double ageDays)
    {
        await using var ctx = db.CreateContext();
        var va = new VirtualAccount(tenantId, customerId, reference, $"90{reference}", "Bank", "Name", expectedAmountKobo: expected);
        if (paid > 0) va.ApplyInflow(Money.FromKobo(paid));
        ctx.VirtualAccounts.Add(va);
        await ctx.SaveChangesAsync();                       // Added → CreatedAtUtc stamped to clock.UtcNow
        va.CreatedAtUtc = db.Clock.UtcNow.AddDays(-ageDays); // Modified → CreatedAtUtc kept as-is
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Aging_buckets_outstanding_receivables_by_account_age()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        db.Tenant.TenantId = tenantId;
        var cust = new Customer(tenantId, "c-1", "Payer");
        await using (var ctx = db.CreateContext()) { ctx.Customers.Add(cust); await ctx.SaveChangesAsync(); }

        await SeedDeficitAccountAsync(db, tenantId, cust.Id, "0001", 100_00, 40_00, ageDays: 3);   // 0–7:   60_00
        await SeedDeficitAccountAsync(db, tenantId, cust.Id, "0002", 100_00, 0, ageDays: 20);       // 8–30:  100_00
        await SeedDeficitAccountAsync(db, tenantId, cust.Id, "0003", 100_00, 25_00, ageDays: 45);   // 31–60: 75_00
        await SeedDeficitAccountAsync(db, tenantId, cust.Id, "0004", 100_00, 0, ageDays: 90);       // 60+:   100_00
        await SeedDeficitAccountAsync(db, tenantId, cust.Id, "0005", 100_00, 100_00, ageDays: 10);  // fully paid → excluded

        await using var check = db.CreateContext();
        var report = await Service(db, check).GetAgingAsync();

        report.Buckets.Should().HaveCount(4);
        report.Buckets[0].Should().BeEquivalentTo(new { Label = "0–7 days", Accounts = 1, OutstandingKobo = 60_00L });
        report.Buckets[1].Should().BeEquivalentTo(new { Label = "8–30 days", Accounts = 1, OutstandingKobo = 100_00L });
        report.Buckets[2].Should().BeEquivalentTo(new { Label = "31–60 days", Accounts = 1, OutstandingKobo = 75_00L });
        report.Buckets[3].Should().BeEquivalentTo(new { Label = "60+ days", Accounts = 1, OutstandingKobo = 100_00L });
        report.TotalOutstandingKobo.Should().Be(335_00);
    }

    [Fact]
    public async Task Forecast_combines_scheduled_billing_due_with_a_run_rate_projection()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        db.Tenant.TenantId = tenantId;
        var now = db.Clock.UtcNow;

        await using (var ctx = db.CreateContext())
        {
            var cust = new Customer(tenantId, "c-1", "Payer");
            var va = new VirtualAccount(tenantId, cust.Id, "acc-1", "9000000000", "Bank", "Name");
            ctx.Customers.Add(cust);
            ctx.VirtualAccounts.Add(va);
            await ctx.SaveChangesAsync();

            // Trailing 30-day deposits: net 300,000 kobo → 10,000/day.
            ctx.Transactions.Add(new Transaction(tenantId, va.Id, "dep-1", "Payer",
                Money.FromKobo(300_000), Money.Zero, TransactionStatus.Success, ReconciliationStatus.Reconciled,
                null, now.AddDays(-5), now.AddDays(-5)));

            // A billing period due in ~10 days, 50,000 outstanding.
            var sched = new BillingSchedule(tenantId, va.Id, cust.Id, "sch-1", BillingInterval.Monthly, 50_000);
            ctx.BillingSchedules.Add(sched);
            await ctx.SaveChangesAsync();
            ctx.BillingPeriods.Add(new BillingPeriod(tenantId, sched.Id, 1, now, now.AddDays(30), now.AddDays(10), 50_000));
            await ctx.SaveChangesAsync();
        }

        await using var check = db.CreateContext();
        var f = await Service(db, check).GetForecastAsync(30);

        f.Days.Should().Be(30);
        f.Weeks.Should().HaveCount(5);                        // ceil(30/7)
        f.ScheduledDueKobo.Should().Be(50_000);
        f.Weeks[1].ScheduledKobo.Should().Be(50_000);         // due day+10 falls in week index 1
        f.DailyRunRateKobo.Should().Be(10_000);
        f.RunRateProjectedKobo.Should().Be(300_000);
        f.ProjectedTotalKobo.Should().Be(350_000);
    }

    [Fact]
    public async Task Forecast_clamps_the_window_to_a_minimum_of_seven_days()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        db.Tenant.TenantId = tenantId;

        await using var check = db.CreateContext();
        var f = await Service(db, check).GetForecastAsync(3);
        f.Days.Should().Be(7);
        f.Weeks.Should().HaveCount(1);
    }

    [Fact]
    public async Task Customer_scores_rank_worst_first_and_rate_reliability()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        db.Tenant.TenantId = tenantId;

        await using (var ctx = db.CreateContext())
        {
            var good = new Customer(tenantId, "good", "Reliable Ltd");
            var bad = new Customer(tenantId, "bad", "Slow Payer");
            var orphan = new Customer(tenantId, "orphan", "No Accounts");   // excluded (no accounts)
            ctx.Customers.AddRange(good, bad, orphan);

            var vaGood = new VirtualAccount(tenantId, good.Id, "g-1", "9000000001", "Bank", "Name", expectedAmountKobo: 100_00);
            vaGood.ApplyInflow(Money.FromKobo(100_00));                     // fully paid → rate 100
            var vaBad = new VirtualAccount(tenantId, bad.Id, "b-1", "9000000002", "Bank", "Name", expectedAmountKobo: 100_00);
            vaBad.ApplyInflow(Money.FromKobo(40_00));                       // 40% paid → 60_00 outstanding
            ctx.VirtualAccounts.AddRange(vaGood, vaBad);
            await ctx.SaveChangesAsync();
        }

        await using var check = db.CreateContext();
        var scores = await Service(db, check).GetCustomerScoresAsync();

        scores.Should().HaveCount(2);                                       // orphan excluded
        scores[0].CustomerRef.Should().Be("bad");                          // most outstanding first
        scores[0].OutstandingKobo.Should().Be(60_00);
        scores[0].Score.Should().Be(40);
        scores[0].Rating.Should().Be("Poor");
        scores[1].CustomerRef.Should().Be("good");
        scores[1].Score.Should().Be(100);
        scores[1].Rating.Should().Be("Excellent");
    }
}
