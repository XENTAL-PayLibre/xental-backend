namespace Xental.Domain.Common;

/// <summary>
/// Fuzzy person/entity name matching shared by reconciliation (payer vs customer) and KYC
/// (government-record name vs submitted name vs bank-account name). Normalized + substring
/// tolerant so "EMMA O." matches "Emma Okonkwo" — not exact string equality.
/// </summary>
public static class NameMatcher
{
    /// <summary>True when the two names plausibly refer to the same person/entity.</summary>
    public static bool IsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        var x = Normalize(a);
        var y = Normalize(b);
        if (x == y || x.Contains(y) || y.Contains(x))
            return true;
        // Token overlap: every token of the shorter name appears in the longer (order-independent).
        var xs = x.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ys = y.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (small, big) = xs.Length <= ys.Length ? (xs, ys) : (ys, xs);
        return small.Length > 0 && small.All(t => big.Contains(t));
    }

    /// <summary>Inverse of <see cref="IsMatch"/> when both names are present; false if either is blank.</summary>
    public static bool IsMismatch(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && !IsMatch(a, b);

    private static string Normalize(string s) =>
        new string(s.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
            .Replace("  ", " ");
}
