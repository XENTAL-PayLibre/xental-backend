using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.ApiKeys;
using Xental.Application.Authentication;
using Xental.Application.Common.Exceptions;
using Xental.Application.Merchants;
using Xental.Application.Tenancy;
using Xental.Domain.Merchants;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Persistence;
using Xental.Infrastructure.Security;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class DeveloperRegistrationServiceTests
{
    [Fact]
    public async Task Register_hashes_password_and_starts_unverified()
    {
        using var db = new TestDatabase();
        var hasher = TestSecurity.PasswordHasher();
        await using var ctx = db.CreateContext();
        var service = new DeveloperRegistrationService(ctx, hasher);

        var result = await service.RegisterAsync("Ada Lovelace", "Ada@Example.com", TestSecurity.StrongPassword);

        result.Email.Should().Be("ada@example.com", "emails are normalized");
        result.EmailVerified.Should().BeFalse("registration does not verify or log in");

        var tenant = await ctx.Tenants.SingleAsync();
        tenant.PasswordHash.Should().NotBeNullOrEmpty().And.NotContain(TestSecurity.StrongPassword);
        hasher.Verify(TestSecurity.StrongPassword, tenant.PasswordHash).Should().BeTrue();
    }

    [Theory]
    [InlineData("short1!A")]           // too short
    [InlineData("alllowercase123!")]   // no uppercase
    [InlineData("ALLUPPERCASE123!")]   // no lowercase
    [InlineData("NoDigitsHere!!!")]    // no digit
    [InlineData("NoSpecials12345")]    // no special char
    public async Task Register_rejects_weak_password(string weak)
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var service = new DeveloperRegistrationService(ctx, TestSecurity.PasswordHasher());

        var act = () => service.RegisterAsync("Ada", "ada@example.com", weak);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Register_duplicate_email_conflicts()
    {
        using var db = new TestDatabase();
        var hasher = TestSecurity.PasswordHasher();
        await using (var ctx = db.CreateContext())
            await new DeveloperRegistrationService(ctx, hasher)
                .RegisterAsync("Ada", "dup@example.com", TestSecurity.StrongPassword);

        await using var ctx2 = db.CreateContext();
        var act = () => new DeveloperRegistrationService(ctx2, hasher)
            .RegisterAsync("Ada 2", "DUP@example.com", TestSecurity.StrongPassword);
        await act.Should().ThrowAsync<ConflictException>();
    }
}

public class DeveloperAuthServiceTests
{
    private static DeveloperAuthService Auth(TestDatabase db, XentalDbContext ctx, FakeEmailSender? email = null) =>
        new(ctx, TestSecurity.PasswordHasher(), new Sha256TokenHasher(), email ?? new FakeEmailSender(), TestSecurity.Sessions(ctx, db.Clock), db.Clock);

    /// <summary>Complete both login steps (password → emailed OTP) and return the issued session.</summary>
    private static async Task<IssuedSession> LoginAsync(TestDatabase db, XentalDbContext ctx, string email, string password)
    {
        var sender = new FakeEmailSender();
        var auth = Auth(db, ctx, sender);
        await auth.BeginLoginAsync(email, password);
        return await auth.VerifyLoginOtpAsync(email, sender.LastOtpCode!);
    }

    private static async Task RegisterAsync(TestDatabase db, string email, bool verified)
    {
        await using var ctx = db.CreateContext();
        var reg = await new DeveloperRegistrationService(ctx, TestSecurity.PasswordHasher())
            .RegisterAsync("Dev", email, TestSecurity.StrongPassword);
        if (verified)
        {
            var t = await ctx.Tenants.SingleAsync(x => x.Id == reg.TenantId);
            t.MarkEmailVerified();
            await ctx.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Verified_login_issues_a_session()
    {
        using var db = new TestDatabase();
        await RegisterAsync(db, "dev@example.com", verified: true);

        await using var ctx = db.CreateContext();
        var session = await LoginAsync(db, ctx, "Dev@example.com", TestSecurity.StrongPassword);

        session.Email.Should().Be("dev@example.com");
        session.Access.Token.Should().NotBeNullOrWhiteSpace();
        session.RefreshToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unverified_login_is_forbidden()
    {
        using var db = new TestDatabase();
        await RegisterAsync(db, "dev@example.com", verified: false);

        await using var ctx = db.CreateContext();
        var act = () => Auth(db, ctx).BeginLoginAsync("dev@example.com", TestSecurity.StrongPassword);
        await act.Should().ThrowAsync<EmailNotVerifiedException>();
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        using var db = new TestDatabase();
        await RegisterAsync(db, "dev@example.com", verified: true);

        await using var ctx = db.CreateContext();
        var act = () => Auth(db, ctx).BeginLoginAsync("dev@example.com", "Wr0ng-Passw0rd!");
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Unknown_email_is_rejected()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var act = () => Auth(db, ctx).BeginLoginAsync("nobody@example.com", TestSecurity.StrongPassword);
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Refresh_rotates_and_old_token_is_rejected()
    {
        using var db = new TestDatabase();
        await RegisterAsync(db, "dev@example.com", verified: true);

        string refresh1;
        await using (var ctx = db.CreateContext())
            refresh1 = (await LoginAsync(db, ctx, "dev@example.com", TestSecurity.StrongPassword)).RefreshToken;

        string refresh2;
        await using (var ctx = db.CreateContext())
            refresh2 = (await Auth(db, ctx).RefreshAsync(refresh1)).RefreshToken;
        refresh2.Should().NotBe(refresh1);

        await using var ctx2 = db.CreateContext();
        var act = () => Auth(db, ctx2).RefreshAsync(refresh1); // consumed token
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        using var db = new TestDatabase();
        await RegisterAsync(db, "dev@example.com", verified: true);

        string refresh;
        await using (var ctx = db.CreateContext())
            refresh = (await LoginAsync(db, ctx, "dev@example.com", TestSecurity.StrongPassword)).RefreshToken;
        await using (var ctx = db.CreateContext())
            await Auth(db, ctx).LogoutAsync(refresh);

        await using var ctx2 = db.CreateContext();
        var act = () => Auth(db, ctx2).RefreshAsync(refresh);
        await act.Should().ThrowAsync<AuthenticationException>();
    }
}

public class ApiKeyServiceTests
{
    private static async Task<Guid> SeedTenantAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var tenant = new Tenant("Acme", $"acme-{Guid.NewGuid():N}@example.com", "hash");
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();
        return tenant.Id;
    }

    [Theory]
    [InlineData(ApiKeyMode.Test, "xnt_test", "sk_test")]
    [InlineData(ApiKeyMode.Live, "xnt_live", "sk_live")]
    public async Task Create_returns_secret_once_and_stores_only_a_hash(ApiKeyMode mode, string clientPrefix, string secretPrefix)
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        // Live keys are gated on an approved onboarding; seed one for the Live case.
        if (mode == ApiKeyMode.Live)
            await OnboardingSeed.ApprovedLiveAsync(db, db.Tenant.TenantId!.Value);
        var hasher = new Pbkdf2SecretHasher();

        await using var ctx = db.CreateContext();
        var service = new ApiKeyService(ctx, db.Tenant, hasher, new FakeTokenGenerator(), db.Clock);
        var created = await service.CreateAsync("default", mode);

        created.ClientId.Should().StartWith(clientPrefix);
        created.ClientSecret.Should().StartWith(secretPrefix);

        var key = await ctx.ApiKeys.SingleAsync();
        key.SecretHash.Should().NotContain(created.ClientSecret);
        hasher.Verify(created.ClientSecret, key.SecretHash).Should().BeTrue();
    }

    [Fact]
    public async Task Revoke_marks_the_key_revoked()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);

        Guid keyId;
        await using (var ctx = db.CreateContext())
        {
            var created = await new ApiKeyService(ctx, db.Tenant, new Pbkdf2SecretHasher(), new FakeTokenGenerator(), db.Clock)
                .CreateAsync("default", ApiKeyMode.Test);
            keyId = created.Id;
        }

        await using (var ctx = db.CreateContext())
            await new ApiKeyService(ctx, db.Tenant, new Pbkdf2SecretHasher(), new FakeTokenGenerator(), db.Clock)
                .RevokeAsync(keyId);

        await using var check = db.CreateContext();
        (await check.ApiKeys.SingleAsync(k => k.Id == keyId)).Status.Should().Be(ApiKeyStatus.Revoked);
    }

    [Fact]
    public async Task Rotate_revokes_the_old_key_and_issues_a_new_active_one()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenantAsync(db);
        await OnboardingSeed.ApprovedLiveAsync(db, db.Tenant.TenantId!.Value); // Live keys require Live tier
        var tokens = new FakeTokenGenerator(); // shared so rotate mints a distinct client id

        Guid oldId;
        await using (var ctx = db.CreateContext())
            oldId = (await new ApiKeyService(ctx, db.Tenant, new Pbkdf2SecretHasher(), tokens, db.Clock)
                .CreateAsync("default", ApiKeyMode.Live)).Id;

        CreatedApiKey rotated;
        await using (var ctx = db.CreateContext())
            rotated = await new ApiKeyService(ctx, db.Tenant, new Pbkdf2SecretHasher(), tokens, db.Clock)
                .RotateAsync(oldId);

        rotated.Id.Should().NotBe(oldId);
        rotated.Mode.Should().Be(ApiKeyMode.Live.ToString());

        await using var check = db.CreateContext();
        (await check.ApiKeys.SingleAsync(k => k.Id == oldId)).Status.Should().Be(ApiKeyStatus.Revoked);
        (await check.ApiKeys.SingleAsync(k => k.Id == rotated.Id)).Status.Should().Be(ApiKeyStatus.Active);
    }
}

public class AuthenticationServiceTests
{
    private static async Task<(string clientId, string secret)> SeedKeyAsync(TestDatabase db, ApiKeyMode mode = ApiKeyMode.Test)
    {
        await using var ctx = db.CreateContext();
        var tenant = new Tenant("Acme", $"acme-{Guid.NewGuid():N}@example.com", "hash");
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();

        db.Tenant.TenantId = tenant.Id;
        var created = await new ApiKeyService(ctx, db.Tenant, new Pbkdf2SecretHasher(), new FakeTokenGenerator(), db.Clock)
            .CreateAsync("default", mode);
        db.Tenant.TenantId = null;
        return (created.ClientId, created.ClientSecret);
    }

    [Fact]
    public async Task Valid_credentials_issue_an_api_token()
    {
        using var db = new TestDatabase();
        var (clientId, secret) = await SeedKeyAsync(db);

        await using var ctx = db.CreateContext();
        var token = await new AuthenticationService(ctx, new Pbkdf2SecretHasher(), TestSecurity.Jwt(), db.Clock)
            .IssueTokenAsync(clientId, secret);

        token.Token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Wrong_secret_is_rejected()
    {
        using var db = new TestDatabase();
        var (clientId, _) = await SeedKeyAsync(db);

        await using var ctx = db.CreateContext();
        var act = () => new AuthenticationService(ctx, new Pbkdf2SecretHasher(), TestSecurity.Jwt(), db.Clock)
            .IssueTokenAsync(clientId, "sk_test_wrong");
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Unknown_client_is_rejected()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var act = () => new AuthenticationService(ctx, new Pbkdf2SecretHasher(), TestSecurity.Jwt(), db.Clock)
            .IssueTokenAsync("xnt_test_nope", "sk_test_nope");
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Revoked_key_is_rejected()
    {
        using var db = new TestDatabase();
        var (clientId, secret) = await SeedKeyAsync(db);

        // revoke it
        await using (var ctx = db.CreateContext())
        {
            var key = await ctx.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.ClientId == clientId);
            key.Revoke(db.Clock.UtcNow);
            await ctx.SaveChangesAsync();
        }

        await using var authCtx = db.CreateContext();
        var act = () => new AuthenticationService(authCtx, new Pbkdf2SecretHasher(), TestSecurity.Jwt(), db.Clock)
            .IssueTokenAsync(clientId, secret);
        await act.Should().ThrowAsync<AuthenticationException>();
    }
}

public class SubMerchantServiceTests
{
    private static async Task<Guid> SeedTenantAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var tenant = new Tenant("Acme", $"acme-{Guid.NewGuid():N}@example.com", "hash");
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();
        return tenant.Id;
    }

    [Fact]
    public async Task Create_makes_an_active_internal_record()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        db.Tenant.TenantId = tenantId;

        await using var ctx = db.CreateContext();
        var sub = await new SubMerchantService(ctx, db.Tenant, new FakeNombaClient()).CreateAsync("Green School", "sch-001");

        sub.Status.Should().Be(SubMerchantStatus.Active);
        sub.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Duplicate_reference_for_same_tenant_conflicts()
    {
        using var db = new TestDatabase();
        var tenantId = await SeedTenantAsync(db);
        db.Tenant.TenantId = tenantId;

        await using (var ctx = db.CreateContext())
            await new SubMerchantService(ctx, db.Tenant, new FakeNombaClient()).CreateAsync("School A", "dup");

        await using var ctx2 = db.CreateContext();
        var act = () => new SubMerchantService(ctx2, db.Tenant, new FakeNombaClient()).CreateAsync("School A again", "dup");
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Tenants_are_isolated_from_each_other()
    {
        using var db = new TestDatabase();
        var tenantA = await SeedTenantAsync(db);
        var tenantB = await SeedTenantAsync(db);

        db.Tenant.TenantId = tenantA;
        await using (var ctx = db.CreateContext())
            await new SubMerchantService(ctx, db.Tenant, new FakeNombaClient()).CreateAsync("A School", "shared-ref");

        // Tenant B cannot see A's data ...
        db.Tenant.TenantId = tenantB;
        await using (var ctx = db.CreateContext())
        {
            var listForB = await new SubMerchantService(ctx, db.Tenant, new FakeNombaClient()).ListAsync();
            listForB.Should().BeEmpty("B must not see A's sub-merchants");
        }

        // ... and B can reuse the same reference (uniqueness is per-tenant)
        await using (var ctx = db.CreateContext())
        {
            var act = () => new SubMerchantService(ctx, db.Tenant, new FakeNombaClient()).CreateAsync("B School", "shared-ref");
            await act.Should().NotThrowAsync();
        }

        // A still sees only its own row
        db.Tenant.TenantId = tenantA;
        await using (var ctx = db.CreateContext())
        {
            var listForA = await new SubMerchantService(ctx, db.Tenant, new FakeNombaClient()).ListAsync();
            listForA.Should().ContainSingle().Which.Name.Should().Be("A School");
        }
    }
}
