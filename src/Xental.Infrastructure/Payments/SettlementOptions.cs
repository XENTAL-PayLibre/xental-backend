namespace Xental.Infrastructure.Payments;

/// <summary>
/// Operational guardrails for the settlement worker. Configurable per environment so real payouts
/// can be capped (or fully paused) without a redeploy — important when first moving live money.
/// </summary>
public sealed class SettlementOptions
{
    public const string SectionName = "Settlement";

    /// <summary>Master switch. When false, the worker settles nothing (funds stay put). Default on.</summary>
    public bool PayoutsEnabled { get; set; } = true;

    /// <summary>Largest single settlement (kobo) the worker will auto-initiate. 0 = no cap. A round that
    /// exceeds it is held for manual review and an operator is alerted, rather than auto-paid.</summary>
    public long MaxPayoutKobo { get; set; }
}
