using Xental.Domain.Tenancy;

namespace Xental.Application.Common.Interfaces;

public sealed record AccessToken(string Token, int ExpiresInSeconds, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues signed JWTs. Two audiences/scopes:
///  - dashboard token (from developer login) for managing the account + keys;
///  - API token (from client-credentials) carrying the key mode (test/live).
/// </summary>
public interface IJwtTokenService
{
    AccessToken IssueDashboardToken(Tenant tenant);
    AccessToken IssueApiToken(Tenant tenant, ApiKey apiKey);
}
