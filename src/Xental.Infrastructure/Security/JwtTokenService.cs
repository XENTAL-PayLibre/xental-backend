using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;
using Xental.Domain.Tenancy;

namespace Xental.Infrastructure.Security;

public sealed class JwtTokenService : IJwtTokenService
{
    // Scopes distinguish the token planes.
    public const string DashboardScope = "dashboard";
    public const string ApiScope = "api";
    public const string AdminScope = "admin";

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

    /// <summary>Token for the developer dashboard (manage account + keys). The account owner is "Owner".</summary>
    public AccessToken IssueDashboardToken(Tenant tenant) =>
        IssueDashboardToken(tenant.Id, tenant.Email, tenant.EmailVerified, "Owner");

    /// <summary>Dashboard token for a team member: the account's tenant id + the member's email + role.</summary>
    public AccessToken IssueDashboardToken(Guid tenantId, string email, bool emailVerified, string role)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, tenantId.ToString()),
            new("tenant_id", tenantId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new("email_verified", emailVerified ? "true" : "false"),
            new("scope", DashboardScope),
            new("team_role", role),
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

    /// <summary>Token for the admin plane, carrying the admin role (Admin / SuperAdmin).</summary>
    public AccessToken IssueAdminToken(AdminUser admin)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new("admin_id", admin.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new("scope", AdminScope),
            new("admin_role", admin.Role.ToString()),
        };
        // Admin sessions are short — reuse the dashboard token lifetime.
        return Issue(claims, _options.DashboardTokenLifetimeSeconds);
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
