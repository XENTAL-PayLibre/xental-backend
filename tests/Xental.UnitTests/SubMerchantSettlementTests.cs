using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Merchants;
using Xental.Domain.Common;
using Xental.Domain.Merchants;
using Xental.Domain.Payments;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Payments;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class SubMerchantSettlementTests
{
    // --- worker harness (mirrors SplitWorkerTests) ---------------------------
    private sealed class Factory(TestDatabase db, INombaClient nomba) : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new Scope(db, nomba);
        private sealed class Scope(TestDatabase db, INombaClient nomba) : Microsoft.Extensions.DependencyInjection.IServiceScope, IServiceProvider
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

    private static SubMerchant NewSub(Guid tenantId, string bankCode, string account, int feeBps)
    {
        var sub = new SubMerchant(tenantId, "School A", "sch-a");
        sub.SetPayout("GTBank", bankCode, account, "School A Ltd", feeBps);
        return sub;
    }

    [Fact]
    public async Task Sub_merchant_account_settles_to_the_sub_merchant_with_the_platform_fee_skimmed()
    {
        using var db = new TestDatabase();
        Guid accountId;
        await using (var ctx = db.CreateContext())
        {
            var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
            var cfg = new SettlementConfig(t.Id);
            cfg.Update("0123456789", "011", "Acme Ltd", autoSettle: true, 0); // operator collects the fee here
            ctx.SettlementConfigs.Add(cfg);
            var sub = NewSub(t.Id, "058", "0000000009", feeBps: 1000); // 10% platform fee
            ctx.SubMerchants.Add(sub);
            var c = new Customer(t.Id, "ref-1", "Payer"); ctx.Customers.Add(c);
            var va = new VirtualAccount(t.Id, c.Id, "ref-1", "1234567890", "Bank", "Payer", expectedAmountKobo: 1_000_00, subMerchantId: sub.Id);
            va.ApplyInflow(Money.FromKobo(1_000_00));
            ctx.VirtualAccounts.Add(va);
            ctx.Transactions.Add(new Transaction(t.Id, va.Id, "x1", "Payer",
                Money.FromKobo(1_000_00), Money.FromKobo(0), TransactionStatus.Success,
                ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
            await ctx.SaveChangesAsync();
            accountId = va.Id;
        }

        await Worker(db, new FakeNombaClient { TransferSucceeds = true }).RunOnceAsync();

        await using var check = db.CreateContext();
        var transfers = await check.Transfers.IgnoreQueryFilters().ToListAsync();
        transfers.Should().HaveCount(2);
        transfers.Single(t => t.RecipientAccountNumber == "0000000009").AmountKobo.Should().Be(900_00, "sub-merchant keeps 90%");
        transfers.Single(t => t.RecipientAccountNumber == "0123456789").AmountKobo.Should().Be(100_00, "operator takes the 10% fee");
        var settled = await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId);
        settled.IsSettled.Should().BeTrue();
        settled.SettledUpToKobo.Should().Be(1_000_00);
    }

    [Fact]
    public async Task Reusable_open_account_settles_incrementally_across_deposits()
    {
        using var db = new TestDatabase();
        Guid accountId;
        await using (var ctx = db.CreateContext())
        {
            var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
            var cfg = new SettlementConfig(t.Id);
            cfg.Update("0123456789", "011", "Acme Ltd", autoSettle: true, 0);
            ctx.SettlementConfigs.Add(cfg);
            var sub = NewSub(t.Id, "058", "0000000009", feeBps: 0); // no fee, 100% to sub-merchant
            ctx.SubMerchants.Add(sub);
            var c = new Customer(t.Id, "ref-1", "Payer"); ctx.Customers.Add(c);
            // OPEN account: no expected amount, reused across deposits.
            var va = new VirtualAccount(t.Id, c.Id, "ref-1", "1234567890", "Bank", "Payer", subMerchantId: sub.Id);
            va.ApplyInflow(Money.FromKobo(500_00));
            ctx.VirtualAccounts.Add(va);
            ctx.Transactions.Add(new Transaction(t.Id, va.Id, "d1", "Payer",
                Money.FromKobo(500_00), Money.FromKobo(0), TransactionStatus.Success,
                ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
            await ctx.SaveChangesAsync();
            accountId = va.Id;
        }

        await Worker(db, new FakeNombaClient { TransferSucceeds = true }).RunOnceAsync();

        // A second deposit arrives on the same reusable account.
        await using (var ctx = db.CreateContext())
        {
            var va = await ctx.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId);
            va.ApplyInflow(Money.FromKobo(300_00));
            ctx.Transactions.Add(new Transaction(va.TenantId, va.Id, "d2", "Payer",
                Money.FromKobo(300_00), Money.FromKobo(0), TransactionStatus.Success,
                ReconciliationStatus.Reconciled, null, db.Clock.UtcNow, db.Clock.UtcNow));
            await ctx.SaveChangesAsync();
        }

        await Worker(db, new FakeNombaClient { TransferSucceeds = true }).RunOnceAsync();

        await using var check = db.CreateContext();
        var transfers = await check.Transfers.IgnoreQueryFilters().OrderBy(t => t.AmountKobo).ToListAsync();
        transfers.Should().HaveCount(2, "each deposit settled once, incrementally");
        transfers.Select(t => t.AmountKobo).Should().BeEquivalentTo(new[] { 300_00L, 500_00L });
        transfers.Should().OnlyContain(t => t.RecipientAccountNumber == "0000000009");
        (await check.VirtualAccounts.IgnoreQueryFilters().FirstAsync(v => v.Id == accountId)).SettledUpToKobo.Should().Be(800_00);
    }

    [Fact]
    public async Task SetPayout_verifies_the_account_and_stores_the_bank_name_and_fee()
    {
        using var db = new TestDatabase();
        Guid tenantId, subId;
        await using (var ctx = db.CreateContext())
        {
            var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
            var sub = new SubMerchant(t.Id, "School A", "sch-a"); ctx.SubMerchants.Add(sub);
            await ctx.SaveChangesAsync();
            tenantId = t.Id; subId = sub.Id;
        }
        db.Tenant.TenantId = tenantId;

        var svc = new SubMerchantService(db.CreateContext(), db.Tenant, new FakeNombaClient { LookupAccountName = "SCHOOL A LIMITED" });
        var updated = await svc.SetPayoutAsync(subId, "GTBank", "058", "0011223344", platformFeeBps: 1500);

        updated.SettlementAccountName.Should().Be("SCHOOL A LIMITED", "the name is taken from the bank lookup, not the caller");
        updated.SettlementAccountNumber.Should().Be("0011223344");
        updated.PlatformFeeBps.Should().Be(1500);
        updated.HasPayoutAccount.Should().BeTrue();
    }
}
