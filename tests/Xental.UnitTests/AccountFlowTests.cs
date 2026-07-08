using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Authentication;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Tenancy;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Persistence;
using Xental.Infrastructure.Security;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class EmailVerificationServiceTests
{
    private static async Task<Guid> SeedTenantAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var tenant = new Tenant("Dev", $"dev-{Guid.NewGuid():N}@example.com", "hash");
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();
        return tenant.Id;
    }

    [Fact]
    public async Task Send_then_verify_marks_the_email_verified()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        var email = new FakeEmailSender();
        var links = new FakeLinkBuilder();

        await using (var ctx = db.CreateContext())
            await new EmailVerificationService(ctx, new FakeTokenGenerator(), new Sha256TokenHasher(), links, email, db.Clock)
                .SendAsync(tenantId);

        email.LastVerificationLink.Should().NotBeNull();

        bool ok;
        await using (var ctx = db.CreateContext())
            ok = await new EmailVerificationService(ctx, new FakeTokenGenerator(), new Sha256TokenHasher(), links, email, db.Clock)
                .VerifyAsync(email.LastVerificationLink!);

        ok.Should().BeTrue();
        await using var check = db.CreateContext();
        (await check.Tenants.SingleAsync(t => t.Id == tenantId)).EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_is_single_use()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        var email = new FakeEmailSender();
        var links = new FakeLinkBuilder();

        await using (var ctx = db.CreateContext())
            await new EmailVerificationService(ctx, new FakeTokenGenerator(), new Sha256TokenHasher(), links, email, db.Clock).SendAsync(tenantId);
        await using (var ctx = db.CreateContext())
            await new EmailVerificationService(ctx, new FakeTokenGenerator(), new Sha256TokenHasher(), links, email, db.Clock).VerifyAsync(email.LastVerificationLink!);

        await using var ctx2 = db.CreateContext();
        var second = await new EmailVerificationService(ctx2, new FakeTokenGenerator(), new Sha256TokenHasher(), links, email, db.Clock)
            .VerifyAsync(email.LastVerificationLink!);
        second.Should().BeFalse("a consumed token cannot be reused");
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        var email = new FakeEmailSender();
        var links = new FakeLinkBuilder { EmailVerificationTtl = TimeSpan.FromMinutes(-1) };

        await using (var ctx = db.CreateContext())
            await new EmailVerificationService(ctx, new FakeTokenGenerator(), new Sha256TokenHasher(), links, email, db.Clock).SendAsync(tenantId);

        await using var ctx2 = db.CreateContext();
        var ok = await new EmailVerificationService(ctx2, new FakeTokenGenerator(), new Sha256TokenHasher(), links, email, db.Clock)
            .VerifyAsync(email.LastVerificationLink!);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_token_returns_false()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var ok = await new EmailVerificationService(ctx, new FakeTokenGenerator(), new Sha256TokenHasher(), new FakeLinkBuilder(), new FakeEmailSender(), db.Clock)
            .VerifyAsync("not-a-real-token");
        ok.Should().BeFalse();
    }
}

public class PasswordResetServiceTests
{
    private const string NewPassword = "New-Passw0rd-99!";

    private static async Task<string> SeedTenantAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var email = $"dev-{Guid.NewGuid():N}@example.com";
        await new DeveloperRegistrationService(ctx, TestSecurity.PasswordHasher())
            .RegisterAsync("Dev", email, TestSecurity.StrongPassword);
        return email;
    }

    private static PasswordResetService Service(TestDatabase db, XentalDbContext ctx, FakeEmailSender email) =>
        new(ctx, new FakeTokenGenerator(), new Sha256TokenHasher(), new FakeLinkBuilder(), email, TestSecurity.PasswordHasher(), db.Clock);

    [Fact]
    public async Task Request_then_reset_changes_the_password()
    {
        using var db = new TestDatabase();
        var email = await SeedTenantAsync(db);
        var mail = new FakeEmailSender();

        await using (var ctx = db.CreateContext())
            await Service(db, ctx, mail).RequestAsync(email);
        mail.LastResetLink.Should().NotBeNull();

        bool ok;
        await using (var ctx = db.CreateContext())
            ok = await Service(db, ctx, mail).ResetAsync(mail.LastResetLink!, NewPassword);
        ok.Should().BeTrue();

        // The stored hash now matches the new password, not the old one.
        var hasher = TestSecurity.PasswordHasher();
        await using var check = db.CreateContext();
        var tenant = await check.Tenants.SingleAsync(t => t.Email == email);
        hasher.Verify(NewPassword, tenant.PasswordHash).Should().BeTrue();
        hasher.Verify(TestSecurity.StrongPassword, tenant.PasswordHash).Should().BeFalse();
    }

    [Fact]
    public async Task Reset_rejects_a_weak_new_password()
    {
        using var db = new TestDatabase();
        var email = await SeedTenantAsync(db);
        var mail = new FakeEmailSender();
        await using (var ctx = db.CreateContext())
            await Service(db, ctx, mail).RequestAsync(email);

        await using var ctx2 = db.CreateContext();
        var act = () => Service(db, ctx2, mail).ResetAsync(mail.LastResetLink!, "weak");
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Reset_token_is_single_use()
    {
        using var db = new TestDatabase();
        var email = await SeedTenantAsync(db);
        var mail = new FakeEmailSender();

        await using (var ctx = db.CreateContext())
            await Service(db, ctx, mail).RequestAsync(email);
        await using (var ctx = db.CreateContext())
            await Service(db, ctx, mail).ResetAsync(mail.LastResetLink!, NewPassword);

        await using var ctx2 = db.CreateContext();
        var second = await Service(db, ctx2, mail).ResetAsync(mail.LastResetLink!, "Another-Passw0rd-1!");
        second.Should().BeFalse();
    }

    [Fact]
    public async Task Request_for_unknown_email_sends_nothing()
    {
        using var db = new TestDatabase();
        var mail = new FakeEmailSender();
        await using var ctx = db.CreateContext();
        await Service(db, ctx, mail).RequestAsync("nobody@example.com");
        mail.LastResetLink.Should().BeNull();
    }

    [Fact]
    public async Task Change_password_requires_the_correct_current_password()
    {
        using var db = new TestDatabase();
        var email = await SeedTenantAsync(db);
        Guid tenantId;
        await using (var ctx = db.CreateContext())
            tenantId = (await ctx.Tenants.SingleAsync(t => t.Email == email)).Id;

        // Wrong current password is rejected.
        await using (var ctx = db.CreateContext())
        {
            var svc = new DeveloperProfileService(ctx, TestSecurity.PasswordHasher());
            var bad = () => svc.ChangePasswordAsync(tenantId, "wrong-password", NewPassword);
            await bad.Should().ThrowAsync<ValidationException>();
        }

        // Correct current password changes it to the new one.
        await using (var ctx = db.CreateContext())
            await new DeveloperProfileService(ctx, TestSecurity.PasswordHasher())
                .ChangePasswordAsync(tenantId, TestSecurity.StrongPassword, NewPassword);

        var hasher = TestSecurity.PasswordHasher();
        await using var check = db.CreateContext();
        var tenant = await check.Tenants.SingleAsync(t => t.Id == tenantId);
        hasher.Verify(NewPassword, tenant.PasswordHash).Should().BeTrue();
        hasher.Verify(TestSecurity.StrongPassword, tenant.PasswordHash).Should().BeFalse();
    }
}

public class OAuthLoginServiceTests
{
    private static OAuthLoginService Service(TestDatabase db, XentalDbContext ctx, IExternalIdentityProvider provider) =>
        new(ctx, TestSecurity.Sessions(ctx, db.Clock), new[] { provider });

    [Fact]
    public async Task First_login_creates_a_verified_account_and_links_it()
    {
        using var db = new TestDatabase();
        var profile = new ExternalUserProfile("google", "google-123", "New.User@Example.com", "New User");
        var provider = new FakeOAuthProvider("google", profile);

        IssuedSession result;
        await using (var ctx = db.CreateContext())
            result = await Service(db, ctx, provider).CompleteAsync("google", "code", "https://app/callback");

        result.Email.Should().Be("new.user@example.com");
        result.EmailVerified.Should().BeTrue("social login proves email ownership");
        result.RefreshToken.Should().NotBeNullOrWhiteSpace();

        await using var check = db.CreateContext();
        (await check.Tenants.CountAsync()).Should().Be(1);
        (await check.ExternalLogins.IgnoreQueryFilters().SingleAsync()).ProviderUserId.Should().Be("google-123");
    }

    [Fact]
    public async Task Repeat_login_reuses_the_same_account()
    {
        using var db = new TestDatabase();
        var provider = new FakeOAuthProvider("github", new ExternalUserProfile("github", "gh-1", "dev@example.com", "Dev"));

        Guid firstTenant;
        await using (var ctx = db.CreateContext())
            firstTenant = (await Service(db, ctx, provider).CompleteAsync("github", "c1", "https://app/callback")).TenantId;
        Guid secondTenant;
        await using (var ctx = db.CreateContext())
            secondTenant = (await Service(db, ctx, provider).CompleteAsync("github", "c2", "https://app/callback")).TenantId;

        secondTenant.Should().Be(firstTenant);
        await using var check = db.CreateContext();
        (await check.Tenants.CountAsync()).Should().Be(1);
        (await check.ExternalLogins.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Login_links_to_an_existing_email_account()
    {
        using var db = new TestDatabase();
        // A password account already exists with this email.
        await using (var ctx = db.CreateContext())
            await new DeveloperRegistrationService(ctx, TestSecurity.PasswordHasher())
                .RegisterAsync("Dev", "dev@example.com", TestSecurity.StrongPassword);

        var provider = new FakeOAuthProvider("google", new ExternalUserProfile("google", "g-9", "dev@example.com", "Dev"));
        await using (var ctx = db.CreateContext())
            await Service(db, ctx, provider).CompleteAsync("google", "code", "https://app/callback");

        await using var check = db.CreateContext();
        (await check.Tenants.CountAsync()).Should().Be(1, "the social login links to the existing account, not a new one");
        (await check.Tenants.SingleAsync()).EmailVerified.Should().BeTrue();
    }
}
