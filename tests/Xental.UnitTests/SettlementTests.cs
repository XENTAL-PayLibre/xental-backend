using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Payments;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Payments;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class SettlementConfigServiceTests
{
    [Fact]
    public async Task Get_returns_disabled_defaults_when_never_configured()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;

        var config = await new SettlementConfigService(ctx, db.Tenant).GetAsync();
        config.AutoSettle.Should().BeFalse();
        config.CanAutoSettle.Should().BeFalse();
    }

    [Fact]
    public async Task Update_persists_and_can_auto_settle_requires_account_and_bank()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;
        var svc = new SettlementConfigService(ctx, db.Tenant);

        // AutoSettle on but no bank details -> not yet settleable.
        var partial = await svc.UpdateAsync(null, null, null, autoSettle: true, minPayoutKobo: 0);
        partial.CanAutoSettle.Should().BeFalse();

        var full = await svc.UpdateAsync("0123456789", "011", "Acme Ltd", autoSettle: true, minPayoutKobo: 500_00);
        full.CanAutoSettle.Should().BeTrue();
        full.MinPayoutKobo.Should().Be(500_00);

        // Re-read through a fresh context: single row, updated in place.
        await using var check = db.CreateContext();
        (await check.SettlementConfigs.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }
}

public class SettlementWorkerTests
{
    /// <summary>Minimal scope factory that hands the worker a fresh context + fakes per pass.</summary>
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
                : t == typeof(IClock) ? db.Clock
                : null;
            public void Dispose() => _ctx.Dispose();
        }
    }

    private static SettlementWorker Worker(TestDatabase db, INombaClient nomba) =>
        new(new Factory(db, nomba), NullLogger<SettlementWorker>.Instance);

    private static async Task<(Guid tenantId, Guid accountId)> SeedFullyPaidAccountAsync(
        TestDatabase db, bool autoSettle, string? account = "0123456789", long minPayoutKobo = 0, long feeKobo = 0)
    {
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "ref-1", "Payer"); ctx.Customers.Add(c);
        var va = new VirtualAccount(t.Id, c.Id, "ref-1", "1234567890", "Bank", "Payer", expectedAmountKobo: 1_000_00);
        va.ApplyInflow(Money.FromKobo(1_000_00)); // -> FullyPaid
        ctx.VirtualAccounts.Add(va);
        ctx.Transactions.Add(new Transaction(t.Id, va.Id, "x1", "Payer",
            Money.FromKobo(1_000_00), Money.FromKobo(feeKobo), TransactionStatus.Success,
            ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
        if (autoSettle)
        {
            var cfg = new SettlementConfig(t.Id);
            cfg.Update(account, account is null ? null : "011", "Acme Ltd", autoSettle: true, minPayoutKobo);
            ctx.SettlementConfigs.Add(cfg);
        }
        await ctx.SaveChangesAsync();
        return (t.Id, va.Id);
    }

    [Fact]
    public async Task Fully_paid_account_is_swept_net_of_fees_when_opted_in()
    {
        using var db = new TestDatabase();
        var (_, accountId) = await SeedFullyPaidAccountAsync(db, autoSettle: true, feeKobo: 50_00);
        var nomba = new FakeNombaClient { TransferSucceeds = true };

        await Worker(db, nomba).RunOnceAsync();

        await using var check = db.CreateContext();
        var va = await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId);
        va.IsSettled.Should().BeTrue();
        var transfer = await check.Transfers.IgnoreQueryFilters().SingleAsync();
        transfer.Status.Should().Be(TransferStatus.Success);
        transfer.AmountKobo.Should().Be(950_00, "net = gross 1000 less 50 fee");
        transfer.MerchantTxRef.Should().Be($"settle-{accountId:N}-0"); // ref carries the round key = from-water-mark (0 for the first round)
    }

    [Fact]
    public async Task Account_is_not_settled_when_tenant_has_not_opted_in()
    {
        using var db = new TestDatabase();
        var (_, accountId) = await SeedFullyPaidAccountAsync(db, autoSettle: false);

        await Worker(db, new FakeNombaClient()).RunOnceAsync();

        await using var check = db.CreateContext();
        (await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId)).IsSettled.Should().BeFalse();
        (await check.Transfers.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Below_min_payout_threshold_is_held_back()
    {
        using var db = new TestDatabase();
        var (_, accountId) = await SeedFullyPaidAccountAsync(db, autoSettle: true, minPayoutKobo: 5_000_00);

        await Worker(db, new FakeNombaClient()).RunOnceAsync();

        await using var check = db.CreateContext();
        (await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId)).IsSettled.Should().BeFalse();
        (await check.Transfers.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Failed_provider_transfer_leaves_account_unsettled_and_is_not_retried_blindly()
    {
        using var db = new TestDatabase();
        var (_, accountId) = await SeedFullyPaidAccountAsync(db, autoSettle: true);
        var nomba = new FakeNombaClient { TransferSucceeds = false };
        var worker = Worker(db, nomba);

        await worker.RunOnceAsync();
        await worker.RunOnceAsync(); // second pass must not create a duplicate transfer

        await using var check = db.CreateContext();
        (await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId)).IsSettled.Should().BeFalse();
        var transfers = await check.Transfers.IgnoreQueryFilters().ToListAsync();
        transfers.Should().ContainSingle();
        transfers[0].Status.Should().Be(TransferStatus.Failed);
    }

    [Fact]
    public async Task Settlement_pass_is_idempotent_across_repeated_runs()
    {
        using var db = new TestDatabase();
        await SeedFullyPaidAccountAsync(db, autoSettle: true);
        var worker = Worker(db, new FakeNombaClient { TransferSucceeds = true });

        await worker.RunOnceAsync();
        await worker.RunOnceAsync();
        await worker.RunOnceAsync();

        await using var check = db.CreateContext();
        (await check.Transfers.IgnoreQueryFilters().CountAsync()).Should().Be(1, "one sweep per account, ever");
    }
}
