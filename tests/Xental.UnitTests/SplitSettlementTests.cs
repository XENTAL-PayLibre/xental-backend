using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Payments;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class SplitCalculatorTests
{
    private static SettlementSplit Pct(int bps, int priority) =>
        new(Guid.NewGuid(), null, $"B{priority}", "0000000000", "011", SplitBasis.Percentage, bps, 0, priority);
    private static SettlementSplit Flat(long kobo, int priority) =>
        new(Guid.NewGuid(), null, $"B{priority}", "0000000000", "011", SplitBasis.Flat, 0, kobo, priority);

    [Fact]
    public void Percentage_legs_sum_exactly_to_net()
    {
        var legs = SplitCalculator.Allocate(1_000_00, new[] { Pct(7000, 0), Pct(3000, 1) });
        legs.Sum(l => l.AmountKobo).Should().Be(1_000_00);
        legs[0].AmountKobo.Should().Be(700_00);
        legs[1].AmountKobo.Should().Be(300_00);
    }

    [Fact]
    public void Rounding_dust_and_shortfall_go_to_the_first_leg()
    {
        // 33.33% x3 = 99.99%; the remainder (dust + the missing 0.01%) lands on leg 0 so it still sums to net.
        var legs = SplitCalculator.Allocate(1_000_01, new[] { Pct(3333, 0), Pct(3333, 1), Pct(3333, 2) });
        legs.Sum(l => l.AmountKobo).Should().Be(1_000_01);
        legs[0].AmountKobo.Should().BeGreaterThan(legs[1].AmountKobo);
    }

    [Fact]
    public void Flat_platform_fee_plus_percentage_remainder()
    {
        // Flat 50.00 platform fee (priority 0) + a 100% beneficiary; remainder (net - fee) to leg 0? No:
        // leg 0 is the fee, leg 1 takes 100% of net, sum would exceed -> so use fee + a 90% split.
        var legs = SplitCalculator.Allocate(1_000_00, new[] { Flat(50_00, 0), Pct(9000, 1) });
        legs.Sum(l => l.AmountKobo).Should().Be(1_000_00);
        legs.Single(l => l.Split.Basis == SplitBasis.Percentage).AmountKobo.Should().Be(900_00);
    }

    [Fact]
    public void Legs_exceeding_net_throw_so_the_caller_can_abort()
    {
        var act = () => SplitCalculator.Allocate(1_000_00, new[] { Flat(600_00, 0), Flat(600_00, 1) });
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void No_splits_yields_no_legs()
    {
        SplitCalculator.Allocate(1_000_00, Array.Empty<SettlementSplit>()).Should().BeEmpty();
    }
}

public class SplitWorkerTests
{
    private sealed class Factory(TestDatabase db, INombaClient nomba) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(db, nomba);
        private sealed class Scope(TestDatabase db, INombaClient nomba) : IServiceScope, IServiceProvider
        {
            private readonly XentalDbContext _ctx = db.CreateContext();
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type t) =>
                t == typeof(IApplicationDbContext) ? _ctx
                : t == typeof(INombaClient) ? nomba
                : t == typeof(IClock) ? db.Clock : null;
            public void Dispose() => _ctx.Dispose();
        }
    }

    private static SettlementWorker Worker(TestDatabase db, INombaClient nomba) =>
        new(new Factory(db, nomba), NullLogger<SettlementWorker>.Instance);

    private static async Task<(Guid tenantId, Guid accountId)> SeedAsync(
        TestDatabase db, Action<XentalDbContext, Guid, Guid>? extra = null)
    {
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "ref-1", "Payer"); ctx.Customers.Add(c);
        var va = new VirtualAccount(t.Id, c.Id, "ref-1", "1234567890", "Bank", "Payer", expectedAmountKobo: 1_000_00);
        va.ApplyInflow(Money.FromKobo(1_000_00));
        ctx.VirtualAccounts.Add(va);
        ctx.Transactions.Add(new Transaction(t.Id, va.Id, "x1", "Payer",
            Money.FromKobo(1_000_00), Money.FromKobo(0), TransactionStatus.Success,
            ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
        var cfg = new SettlementConfig(t.Id);
        cfg.Update("0123456789", "011", "Acme Ltd", autoSettle: true, 0);
        ctx.SettlementConfigs.Add(cfg);
        extra?.Invoke(ctx, t.Id, va.Id);
        await ctx.SaveChangesAsync();
        return (t.Id, va.Id);
    }

    [Fact]
    public async Task Splits_fan_the_net_out_into_legs_summing_to_net()
    {
        using var db = new TestDatabase();
        var (_, accountId) = await SeedAsync(db, (ctx, tid, _) =>
        {
            ctx.SettlementSplits.Add(new SettlementSplit(tid, null, "Merchant", "0000000001", "011", SplitBasis.Percentage, 8000, 0, 0));
            ctx.SettlementSplits.Add(new SettlementSplit(tid, null, "Platform", "0000000002", "011", SplitBasis.Percentage, 2000, 0, 1));
        });

        await Worker(db, new FakeNombaClient { TransferSucceeds = true }).RunOnceAsync();

        await using var check = db.CreateContext();
        var transfers = await check.Transfers.IgnoreQueryFilters().OrderBy(t => t.MerchantTxRef).ToListAsync();
        transfers.Should().HaveCount(2);
        transfers.Sum(t => t.AmountKobo).Should().Be(1_000_00, "legs sum to exactly net");
        transfers.Select(t => t.MerchantTxRef).Should().BeEquivalentTo(new[] { $"settle-{accountId:N}-0", $"settle-{accountId:N}-1" });
        (await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId)).IsSettled.Should().BeTrue();
    }

    [Fact]
    public async Task Active_escrow_hold_blocks_settlement_until_released()
    {
        using var db = new TestDatabase();
        var (_, accountId) = await SeedAsync(db, (ctx, tid, vid) =>
            ctx.EscrowHolds.Add(new EscrowHold(tid, vid, 1_000_00, "await delivery")));

        await Worker(db, new FakeNombaClient { TransferSucceeds = true }).RunOnceAsync();

        await using (var check = db.CreateContext())
        {
            (await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId)).IsSettled.Should().BeFalse();
            (await check.Transfers.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        }

        // Release the hold, then the next pass settles.
        await using (var rel = db.CreateContext())
        {
            var hold = await rel.EscrowHolds.IgnoreQueryFilters().FirstAsync(e => e.VirtualAccountId == accountId);
            hold.Release(db.Clock.UtcNow);
            await rel.SaveChangesAsync();
        }
        await Worker(db, new FakeNombaClient { TransferSucceeds = true }).RunOnceAsync();

        await using var post = db.CreateContext();
        (await post.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId)).IsSettled.Should().BeTrue();
    }

    [Fact]
    public async Task Split_settlement_is_idempotent_across_runs()
    {
        using var db = new TestDatabase();
        await SeedAsync(db, (ctx, tid, _) =>
        {
            ctx.SettlementSplits.Add(new SettlementSplit(tid, null, "A", "0000000001", "011", SplitBasis.Percentage, 5000, 0, 0));
            ctx.SettlementSplits.Add(new SettlementSplit(tid, null, "B", "0000000002", "011", SplitBasis.Percentage, 5000, 0, 1));
        });
        var worker = Worker(db, new FakeNombaClient { TransferSucceeds = true });

        await worker.RunOnceAsync();
        await worker.RunOnceAsync();

        await using var check = db.CreateContext();
        (await check.Transfers.IgnoreQueryFilters().CountAsync()).Should().Be(2, "two legs, once each");
    }
}
