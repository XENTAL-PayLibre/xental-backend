using Xental.Domain.Common;

namespace Xental.Domain.Tenancy;

/// <summary>
/// Links a developer account to an external identity provider (Google, GitHub).
/// A tenant may have several linked logins; a given (provider, provider user id)
/// pair maps to exactly one tenant.
/// </summary>
public sealed class ExternalLogin : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Provider { get; private set; } = null!;
    public string ProviderUserId { get; private set; } = null!;

    private ExternalLogin() { } // EF

    public ExternalLogin(Guid tenantId, string provider, string providerUserId)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Provider = DomainException.Require(provider, nameof(provider)).ToLowerInvariant();
        ProviderUserId = DomainException.Require(providerUserId, nameof(providerUserId));
    }
}
