using FluentAssertions;
using Microsoft.Extensions.Options;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Security;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class SecretHasherTests
{
    private readonly Pbkdf2SecretHasher _hasher = new();

    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = _hasher.Hash("super-secret");
        _hasher.Verify("super-secret", hash).Should().BeTrue();
    }

    [Fact]
    public void Wrong_secret_fails()
    {
        var hash = _hasher.Hash("super-secret");
        _hasher.Verify("wrong", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_is_salted_so_two_hashes_differ()
    {
        _hasher.Hash("same").Should().NotBe(_hasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-valid-hash")]
    [InlineData("1.2")]
    public void Malformed_hash_returns_false_not_throws(string badHash)
    {
        _hasher.Verify("secret", badHash).Should().BeFalse();
    }
}

public class TokenGeneratorTests
{
    [Fact]
    public void Tokens_are_prefixed_url_safe_and_unique()
    {
        var gen = new SecureTokenGenerator();
        var a = gen.Generate("xnt");
        var b = gen.Generate("xnt");

        a.Should().StartWith("xnt_");
        a.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        a.Should().NotBe(b);
    }
}

public class JwtTokenServiceTests
{
    private static readonly FakeClock Clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    [Fact]
    public void Dashboard_token_carries_email_and_dashboard_scope()
    {
        var service = TestSecurity.Jwt(Clock);
        var tenant = new Tenant("Acme", "owner@example.com", "hash");

        var token = service.IssueDashboardToken(tenant);

        token.ExpiresInSeconds.Should().Be(3600);
        token.ExpiresAt.Should().Be(Clock.UtcNow.AddSeconds(3600));

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token.Token);
        jwt.Issuer.Should().Be("xental");
        jwt.Claims.Should().Contain(c => c.Type == "tenant_id" && c.Value == tenant.Id.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "email" && c.Value == "owner@example.com");
        jwt.Claims.Should().Contain(c => c.Type == "scope" && c.Value == "dashboard");
    }

    [Fact]
    public void Api_token_carries_api_scope_and_key_mode()
    {
        var service = TestSecurity.Jwt(Clock);
        var tenant = new Tenant("Acme", "owner@example.com", "hash");
        var key = new ApiKey(tenant.Id, "xnt_live_abc", "secret-hash", "default", ApiKeyMode.Live);

        var token = service.IssueApiToken(tenant, key);

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token.Token);
        jwt.Claims.Should().Contain(c => c.Type == "tenant_id" && c.Value == tenant.Id.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "scope" && c.Value == "api");
        jwt.Claims.Should().Contain(c => c.Type == "key_mode" && c.Value == "live");
        jwt.Claims.Should().Contain(c => c.Type == "kid" && c.Value == key.Id.ToString());
    }

    [Fact]
    public void Short_signing_key_is_rejected()
    {
        var options = Options.Create(new JwtOptions { SigningKey = "too-short" });
        var act = () => new JwtTokenService(options, new FakeClock(DateTimeOffset.UtcNow));
        act.Should().Throw<InvalidOperationException>();
    }
}
