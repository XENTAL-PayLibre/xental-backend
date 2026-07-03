namespace Xental.Application.Common;

/// <summary>
/// Tier volume limits. A per-tenant daily payout ceiling (kobo); 0 disables the cap. Sandbox tenants
/// can't move real money at all (the live-key gate), so this bounds live payout volume.
/// </summary>
public sealed class TierLimitOptions
{
    public const string SectionName = "TierLimits";

    /// <summary>Max total successful outbound payout per tenant per UTC day, in kobo. 0 = unlimited.</summary>
    public long DailyPayoutCapKobo { get; set; }
}
