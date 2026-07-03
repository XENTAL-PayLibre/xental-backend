using Xental.Domain.Admin;
using Xental.Domain.Tenancy;

namespace Xental.Application.Common.Interfaces;

public sealed record AccessToken(string Token, int ExpiresInSeconds, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues signed JWTs. Scopes/planes:
///  - dashboard token (from developer login) for managing the account + keys;
///  - API token (from client-credentials) carrying the key mode (test/live);
///  - admin token (from admin login + MFA) carrying the admin role.
/// </summary>
public interface IJwtTokenService
{
    AccessToken IssueDashboardToken(Tenant tenant);
    AccessToken IssueApiToken(Tenant tenant, ApiKey apiKey);
    AccessToken IssueAdminToken(AdminUser admin);
}
