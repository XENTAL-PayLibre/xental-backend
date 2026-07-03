using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>Computes settlement legs that sum <b>exactly</b> to the net collected amount.</summary>
public static class SplitCalculator
{
    public sealed record Leg(SettlementSplit Split, long AmountKobo);

    /// <summary>
    /// Allocate <paramref name="netKobo"/> across the enabled splits in priority order. Each leg
    /// gets its base amount (percentage of net, or flat); any rounding dust or unallocated remainder
    /// is added to the first leg (lowest priority number — typically the merchant's primary account)
    /// so the legs always sum to <b>exactly</b> net. Throws when the base amounts already exceed net
    /// (misconfiguration), so the caller can abort the whole settlement — never a partial payout.
    /// </summary>
    public static IReadOnlyList<Leg> Allocate(long netKobo, IEnumerable<SettlementSplit> splits)
    {
        var ordered = splits.Where(s => s.Enabled)
            .OrderBy(s => s.Priority).ThenBy(s => s.CreatedAtUtc)
            .ToList();
        if (ordered.Count == 0)
            return Array.Empty<Leg>();

        var legs = ordered.Select(s => new Leg(s, s.BaseAmountKobo(netKobo))).ToList();
        var sum = legs.Sum(l => l.AmountKobo);
        if (sum > netKobo)
            throw new DomainException($"Split legs ({sum} kobo) exceed the net amount ({netKobo} kobo).");

        var remainder = netKobo - sum; // >= 0: rounding dust + any percentage under 100%
        if (remainder > 0)
            legs[0] = legs[0] with { AmountKobo = legs[0].AmountKobo + remainder };

        return legs;
    }
}
