namespace Xental.Infrastructure.Nomba;

public sealed class NombaOptions
{
    public const string SectionName = "Nomba";

    public string BaseUrl { get; set; } = "https://api.nomba.com/v1/";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Parent account id, sent as the `accountId` header on every request.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The operator's platform sub-account id. Xental scopes virtual-account
    /// (NUBAN) creation to this sub-account in Phase 2. Set once by the operator.</summary>
    public string SubAccountId { get; set; } = string.Empty;

    /// <summary>Serve a cached token until this many seconds have passed. Nomba tokens
    /// expire in 30 min, so refresh ~5 min early (default 25 min).</summary>
    public int TokenRefreshSeconds { get; set; } = 1500;

    /// <summary>Shared secret used to verify inbound Nomba webhook signatures (HMAC-SHA256).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Header carrying the webhook signature.</summary>
    public string WebhookSignatureHeader { get; set; } = "nomba-signature";

    /// <summary>Reject webhooks whose <c>nomba-timestamp</c> is older than this many minutes (replay
    /// bound). Deliberately generous so delayed provider retries still pass; 0 disables the check.
    /// Unparseable timestamps are never rejected on this basis.</summary>
    public int WebhookMaxAgeMinutes { get; set; } = 1440;
}
