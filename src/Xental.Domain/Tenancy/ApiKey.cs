using Xental.Domain.Common;

namespace Xental.Domain.Tenancy;

public enum ApiKeyMode
{
    Test = 1,   // scopes downstream calls to Nomba sandbox
    Live = 2,   // scopes downstream calls to Nomba live
}

public enum ApiKeyStatus
{
    Active = 1,
    Revoked = 2,
}

/// <summary>
/// An API credential owned by a tenant (developer account). The client secret is
/// shown once at creation and stored only as a hash. Keys are per-mode (test/live)
/// and can be revoked/rotated. The mode is carried into the issued API token so
/// downstream integrations use the matching Nomba environment.
/// </summary>
public sealed class ApiKey : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string ClientId { get; private set; } = null!;
    public string SecretHash { get; private set; } = null!;
    public string Label { get; private set; } = null!;
    public ApiKeyMode Mode { get; private set; }
    public ApiKeyStatus Status { get; private set; }
    public DateTimeOffset? LastUsedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    private ApiKey() { } // EF

    public ApiKey(Guid tenantId, string clientId, string secretHash, string label, ApiKeyMode mode)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        ClientId = DomainException.Require(clientId, nameof(clientId));
        SecretHash = DomainException.Require(secretHash, nameof(secretHash));
        Label = DomainException.Require(label, nameof(label));
        Mode = mode;
        Status = ApiKeyStatus.Active;
    }

    public bool IsActive => Status == ApiKeyStatus.Active;

    public void MarkUsed(DateTimeOffset at) => LastUsedAtUtc = at;

    public void Revoke(DateTimeOffset at)
    {
        Status = ApiKeyStatus.Revoked;
        RevokedAtUtc = at;
    }
}
