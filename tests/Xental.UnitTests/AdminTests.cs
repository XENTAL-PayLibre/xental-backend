using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Admin;
using Xental.Application.Common.Exceptions;
using Xental.Domain.Admin;
using Xental.Domain.Onboarding;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Security;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class AdminAuthServiceTests
{
    private static AdminAuthService Service(TestDatabase db, Xental.Infrastructure.Persistence.XentalDbContext ctx) =>
        new(ctx, TestSecurity.PasswordHasher(), new Totp(), TestProtector.Create(), TestSecurity.Jwt(db.Clock), db.Clock);

    private static async Task SeedAdmin(TestDatabase db, string email, string password, bool withMfa = false)
    {
        await using var ctx = db.CreateContext();
        var admin = new AdminUser(email, TestSecurity.PasswordHasher().Hash(password), AdminRole.SuperAdmin);
        if (withMfa) admin.EnrollMfa(TestProtector.Create().Protect(new Totp().GenerateSecret()));
        ctx.AdminUsers.Add(admin);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Valid_credentials_without_mfa_issue_an_admin_token()
    {
        using var db = new TestDatabase();
        await SeedAdmin(db, "boss@x.com", TestSecurity.StrongPassword);
        await using var ctx = db.CreateContext();

        var result = await Service(db, ctx).LoginAsync("BOSS@x.com", TestSecurity.StrongPassword, null);
        result.Token.Token.Should().NotBeNullOrEmpty();
        result.Role.Should().Be("SuperAdmin");
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        using var db = new TestDatabase();
        await SeedAdmin(db, "boss@x.com", TestSecurity.StrongPassword);
        await using var ctx = db.CreateContext();

        var act = () => Service(db, ctx).LoginAsync("boss@x.com", "wrong-password", null);
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Mfa_enrolled_admin_must_provide_a_valid_totp_code()
    {
        using var db = new TestDatabase();
        await SeedAdmin(db, "boss@x.com", TestSecurity.StrongPassword, withMfa: true);
        await using var ctx = db.CreateContext();

        var noCode = () => Service(db, ctx).LoginAsync("boss@x.com", TestSecurity.StrongPassword, null);
        await noCode.Should().ThrowAsync<AuthenticationException>();

        var badCode = () => Service(db, ctx).LoginAsync("boss@x.com", TestSecurity.StrongPassword, "000000");
        await badCode.Should().ThrowAsync<AuthenticationException>();
    }
}

public class AdminOnboardingReviewTests
{
    private static async Task<Guid> SeedTenantUnderReview(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();

        db.Tenant.TenantId = t.Id; // set so the tenant-owned application passes write enforcement
        var app = new OnboardingApplication(t.Id);
        app.SubmitTrack(OnboardingTrack.DeveloperKyc, db.Clock.UtcNow);
        app.SubmitTrack(OnboardingTrack.BusinessKyb, db.Clock.UtcNow);
        ctx.OnboardingApplications.Add(app);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private static AdminOnboardingService Service(TestDatabase db, Xental.Infrastructure.Persistence.XentalDbContext ctx, Guid adminId) =>
        new(ctx, new FakeAdminContext { AdminId = adminId, Role = AdminRole.SuperAdmin }, new FakeDocumentStorage(), db.Clock);

    [Fact]
    public async Task Approving_both_tracks_promotes_the_tenant_to_live_and_audits()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantUnderReview(db);
        var adminId = Guid.NewGuid();

        db.Tenant.TenantId = null; // admin plane is not tenant-scoped
        await using (var ctx = db.CreateContext())
        {
            var svc = Service(db, ctx, adminId);
            await svc.ApproveAsync(tenantId, OnboardingTrack.DeveloperKyc);
            await svc.ApproveAsync(tenantId, OnboardingTrack.BusinessKyb);
        }

        await using var check = db.CreateContext();
        var app = await check.OnboardingApplications.IgnoreQueryFilters().FirstAsync(a => a.TenantId == tenantId);
        app.Tier.Should().Be(KycTier.Live);
        app.DeveloperKycStatus.Should().Be(TrackStatus.Approved);
        app.BusinessKybStatus.Should().Be(TrackStatus.Approved);
        (await check.AdminAuditLogs.CountAsync()).Should().Be(2, "each approval is audited");
    }

    [Fact]
    public async Task Reject_requires_a_reason()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantUnderReview(db);
        db.Tenant.TenantId = null;
        await using var ctx = db.CreateContext();
        var svc = Service(db, ctx, Guid.NewGuid());

        var act = () => svc.RejectAsync(tenantId, OnboardingTrack.DeveloperKyc, "  ");
        await act.Should().ThrowAsync<ValidationException>();
    }
}

public class AdminReconciliationServiceTests
{
    private static Xental.Domain.Payments.Transaction Txn(
        Guid? tenantId, Xental.Domain.Payments.ReconciliationStatus recon,
        Xental.Domain.Payments.TransactionFlag? flag, int risk, DateTimeOffset at) =>
        new(tenantId, tenantId is null ? null : Guid.NewGuid(), $"ref-{Guid.NewGuid():N}", "Payer",
            Xental.Domain.Common.Money.FromKobo(1000), Xental.Domain.Common.Money.Zero,
            Xental.Domain.Payments.TransactionStatus.Success, recon, flag, at, null, risk);

    [Fact]
    public async Task Buckets_summarize_and_filter_the_reconciliation_exceptions()
    {
        using var db = new TestDatabase();
        var now = db.Clock.UtcNow;
        await using (var ctx = db.CreateContext())
        {
            ctx.Transactions.Add(Txn(Guid.NewGuid(), Xental.Domain.Payments.ReconciliationStatus.PendingReview, Xental.Domain.Payments.TransactionFlag.ManualReview, 80, now));
            ctx.Transactions.Add(Txn(null, Xental.Domain.Payments.ReconciliationStatus.PendingReview, Xental.Domain.Payments.TransactionFlag.InvalidAccount, 0, now));
            ctx.Transactions.Add(Txn(Guid.NewGuid(), Xental.Domain.Payments.ReconciliationStatus.Overpaid, Xental.Domain.Payments.TransactionFlag.Overpaid, 10, now));
            ctx.Transactions.Add(Txn(Guid.NewGuid(), Xental.Domain.Payments.ReconciliationStatus.Reversed, Xental.Domain.Payments.TransactionFlag.Reversed, 0, now));
            await ctx.SaveChangesAsync();
        }

        await using var check = db.CreateContext();
        var svc = new AdminReconciliationService(check, new FakeAdminContext(), db.Clock);

        var summary = await svc.SummaryAsync();
        summary.Review.Should().Be(2);   // both PendingReview (one is also the unknown)
        summary.Unknown.Should().Be(1);
        summary.Overpaid.Should().Be(1);
        summary.HighRisk.Should().Be(1);
        summary.Reversals.Should().Be(1);

        (await svc.ListAsync("unknown")).Should().ContainSingle();
        (await svc.ListAsync("highrisk")).Should().ContainSingle();
        var bad = () => svc.ListAsync("nope");
        await bad.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Retry_settlement_clears_the_failed_sweep_so_the_worker_reattempts()
    {
        using var db = new TestDatabase();
        var vaId = Guid.NewGuid();
        Guid tenantId;
        await using (var seed = db.CreateContext())
        {
            var tenant = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
        }
        db.Tenant.TenantId = tenantId;
        await using (var ctx = db.CreateContext())
        {
            var t = new Xental.Domain.Payments.Transfer(tenantId, $"settle-{vaId:N}",
                Xental.Domain.Common.Money.FromKobo(5000), "0123456789", "011", "Acme", "settlement");
            t.MarkFailed("declined", db.Clock.UtcNow);
            ctx.Transfers.Add(t);
            await ctx.SaveChangesAsync();
        }

        db.Tenant.TenantId = null; // admin plane
        await using (var ctx = db.CreateContext())
            await new AdminReconciliationService(ctx, new FakeAdminContext(), db.Clock).RetrySettlementAsync(vaId);

        await using var check = db.CreateContext();
        (await check.Transfers.IgnoreQueryFilters().CountAsync()).Should().Be(0, "the failed sweep is cleared");
        (await check.AdminAuditLogs.CountAsync()).Should().Be(1);
    }
}

public class AdminManagementServiceTests
{
    private static AdminManagementService Service(TestDatabase db, Xental.Infrastructure.Persistence.XentalDbContext ctx, Guid actingAdmin) =>
        new(ctx, TestSecurity.PasswordHasher(), new Totp(), TestProtector.Create(),
            new FakeAdminContext { AdminId = actingAdmin, Role = AdminRole.SuperAdmin }, db.Clock);

    [Fact]
    public async Task SuperAdmin_creates_an_admin_and_duplicate_email_conflicts()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = null;
        await using var ctx = db.CreateContext();
        var svc = Service(db, ctx, Guid.NewGuid());

        var id = await svc.CreateAdminAsync("ops@x.com", TestSecurity.StrongPassword, AdminRole.Admin);
        id.Should().NotBeEmpty();

        var dup = () => svc.CreateAdminAsync("OPS@x.com", TestSecurity.StrongPassword, AdminRole.Admin);
        await dup.Should().ThrowAsync<ConflictException>();

        var weak = () => svc.CreateAdminAsync("weak@x.com", "short", AdminRole.Admin);
        await weak.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Enroll_mfa_sets_the_secret_and_returns_an_otpauth_uri()
    {
        using var db = new TestDatabase();
        Guid adminId;
        await using (var ctx = db.CreateContext())
        {
            var a = new AdminUser("me@x.com", TestSecurity.PasswordHasher().Hash(TestSecurity.StrongPassword), AdminRole.SuperAdmin);
            ctx.AdminUsers.Add(a);
            await ctx.SaveChangesAsync();
            adminId = a.Id;
        }

        db.Tenant.TenantId = null;
        await using var ctx2 = db.CreateContext();
        var uri = await Service(db, ctx2, adminId).EnrollMfaAsync();
        uri.Should().StartWith("otpauth://totp/");

        await using var check = db.CreateContext();
        (await check.AdminUsers.FirstAsync(a => a.Id == adminId)).MfaEnabled.Should().BeTrue();
    }
}
