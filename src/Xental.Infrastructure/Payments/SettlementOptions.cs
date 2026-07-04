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

    /// <summary>How many times a failed payout is auto-retried before it's parked for manual review.
    /// 0 disables auto-retry (failures wait for a human). Default 3.</summary>
    public int MaxPayoutRetries { get; set; } = 3;

    /// <summary>Base back-off (minutes) between payout retries; grows linearly with the attempt number
    /// (retry 1 waits 1×, retry 2 waits 2×, …). Default 5 minutes.</summary>
    public int RetryBackoffMinutes { get; set; } = 5;
}
