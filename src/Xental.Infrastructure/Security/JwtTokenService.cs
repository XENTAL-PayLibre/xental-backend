using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Security;

public sealed class JwtTokenService : IJwtTokenService
{
    // Scopes distinguish the two token planes.
    public const string DashboardScope = "dashboard";
    public const string ApiScope = "api";

    private readonly JwtOptions _options;
    private readonly IClock _clock;
    private readonly SigningCredentials _credentials;

    public JwtTokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;

        if (Encoding.UTF8.GetByteCount(_options.SigningKey) < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    /// <summary>Token for the developer dashboard (manage account + keys).</summary>
    public AccessToken IssueDashboardToken(Tenant tenant)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, tenant.Id.ToString()),
            new("tenant_id", tenant.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, tenant.Email),
            new("email_verified", tenant.EmailVerified ? "true" : "false"),
            new("scope", DashboardScope),
        };
        return Issue(claims, _options.DashboardTokenLifetimeSeconds);
    }

    /// <summary>Token for the integration API, carrying the key mode (test/live).</summary>
    public AccessToken IssueApiToken(Tenant tenant, ApiKey apiKey)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, tenant.Id.ToString()),
            new("tenant_id", tenant.Id.ToString()),
            new("scope", ApiScope),
            new("key_mode", apiKey.Mode.ToString().ToLowerInvariant()),
            new("kid", apiKey.Id.ToString()),
        };
        return Issue(claims, _options.AccessTokenLifetimeSeconds);
    }

    private AccessToken Issue(List<Claim> claims, int lifetimeSeconds)
    {
        var now = _clock.UtcNow;
        var expires = now.AddSeconds(lifetimeSeconds);
        claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _credentials);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return new AccessToken(jwt, lifetimeSeconds, expires);
    }
}
