using Xental.Domain.Common;

namespace Xental.Domain.Webhooks;

/// <summary>
/// A developer-registered callback URL that receives enriched, signed events (reconciled
/// deposits, etc.). The per-endpoint signing secret is stored encrypted at rest and used to
/// compute the outbound <c>x-xental-signature</c>. URLs are SSRF-guarded at registration.
/// </summary>
public sealed class WebhookEndpoint : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Url { get; private set; } = null!;
    public string SecretEncrypted { get; private set; } = null!;
    public bool Active { get; private set; }

    private WebhookEndpoint() { } // EF

    public WebhookEndpoint(Guid tenantId, string url, string secretEncrypted)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Url = DomainException.Require(url, nameof(url));
        SecretEncrypted = DomainException.Require(secretEncrypted, nameof(secretEncrypted));
        Active = true;
    }

    public void Deactivate() => Active = false;
}
