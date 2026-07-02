using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.ApiKeys;
using Xental.Application.Authentication;
using Xental.Application.Common.Exceptions;
using Xental.Application.Merchants;
using Xental.Application.Tenancy;
using Xental.Domain.Merchants;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Security;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class DeveloperRegistrationServiceTests
{
    [Fact]
    public async Task Register_hashes_password_and_returns_dashboard_token()
    {
        using var db = new TestDatabase();
        var hasher = TestSecurity.PasswordHasher();
        await using var ctx = db.CreateContext();
        var service = new DeveloperRegistrationService(ctx, hasher, TestSecurity.Jwt());

        var result = await service.RegisterAsync("Ada Lovelace", "Ada@Example.com", "correct-horse-battery");

        result.Email.Should().Be("ada@example.com", "emails are normalized");
        result.EmailVerified.Should().BeFalse();
        result.DashboardToken.Token.Should().NotBeNullOrWhiteSpace();

        var tenant = await ctx.Tenants.SingleAsync();
        tenant.PasswordHash.Should().NotBeNullOrEmpty().And.NotContain("correct-horse-battery");
        hasher.Verify("correct-horse-battery", tenant.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task Register_rejects_short_password()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var service = new DeveloperRegistrationService(ctx, TestSecurity.PasswordHasher(), TestSecurity.Jwt());

        var act = () => service.RegisterAsync("Ada", "ada@example.com", "short");
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Register_duplicate_email_conflicts()
    {
        using var db = new TestDatabase();
        var hasher = TestSecurity.PasswordHasher();
        await using (var ctx = db.CreateContext())
            await new DeveloperRegistrationService(ctx, hasher, TestSecurity.Jwt())
                .RegisterAsync("Ada", "dup@example.com", "correct-horse-battery");

        await using var ctx2 = db.CreateContext();
        var act = () => new DeveloperRegistrationService(ctx2, hasher, TestSecurity.Jwt())
            .RegisterAsync("Ada 2", "DUP@example.com", "another-long-password");
        await act.Should().ThrowAsync<ConflictException>();
    }
}

public class DeveloperAuthServiceTests
{
    private static async Task RegisterAsync(TestDatabase db, string email, string password)
    {
        await using var ctx = db.CreateContext();
        await new DeveloperRegistrationService(ctx, TestSecurity.PasswordHasher(), TestSecurity.Jwt())
            .RegisterAsync("Dev", email, password);
    }

    [Fact]
    public async Task Valid_login_returns_dashboard_token()
    {
        using var db = new TestDatabase();
        await RegisterAsync(db, "dev@example.com", "correct-horse-battery");

        await using var ctx = db.CreateContext();
        var result = await new DeveloperAuthService(ctx, TestSecurity.PasswordHasher(), TestSecurity.Jwt())
            .LoginAsync("Dev@example.com", "correct-horse-battery");

        result.Email.Should().Be("dev@example.com");
        result.Token.Token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        using var db = new TestDatabase();
        await RegisterAsync(db, "dev@example.com", "correct-horse-battery");

        await using var ctx = db.CreateContext();
        var act = () => new DeveloperAuthService(ctx, TestSecurity.PasswordHasher(), TestSecurity.Jwt())
            .LoginAsync("dev@example.com", "wrong-password-here");
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task Unknown_email_is_rejected()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var act = () => new DeveloperAuthService(ctx, TestSecurity.PasswordHasher(), TestSecurity.Jwt())
            .LoginAsync("nobody@example.com", "correct-horse-battery");
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
        var sub = await new SubMerchantService(ctx, db.Tenant).CreateAsync("Green School", "sch-001");

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
            await new SubMerchantService(ctx, db.Tenant).CreateAsync("School A", "dup");

        await using var ctx2 = db.CreateContext();
        var act = () => new SubMerchantService(ctx2, db.Tenant).CreateAsync("School A again", "dup");
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
            await new SubMerchantService(ctx, db.Tenant).CreateAsync("A School", "shared-ref");

        // Tenant B cannot see A's data ...
        db.Tenant.TenantId = tenantB;
        await using (var ctx = db.CreateContext())
        {
            var listForB = await new SubMerchantService(ctx, db.Tenant).ListAsync();
            listForB.Should().BeEmpty("B must not see A's sub-merchants");
        }

        // ... and B can reuse the same reference (uniqueness is per-tenant)
        await using (var ctx = db.CreateContext())
        {
            var act = () => new SubMerchantService(ctx, db.Tenant).CreateAsync("B School", "shared-ref");
            await act.Should().NotThrowAsync();
        }

        // A still sees only its own row
        db.Tenant.TenantId = tenantA;
        await using (var ctx = db.CreateContext())
        {
            var listForA = await new SubMerchantService(ctx, db.Tenant).ListAsync();
            listForA.Should().ContainSingle().Which.Name.Should().Be("A School");
        }
    }
}
