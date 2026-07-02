namespace Xental.Domain.Common;

/// <summary>
/// Money as an integer count of the minor unit (kobo). NEVER use floating point
/// for money. Amounts are checked for overflow. Currency is NGN by convention.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public long Kobo { get; }

    public Money(long kobo) => Kobo = kobo;

    public static readonly Money Zero = new(0);

    public static Money FromKobo(long kobo) => new(kobo);

    /// <summary>Convert naira (decimal) to kobo, banker's rounding to the nearest kobo.</summary>
    public static Money FromNaira(decimal naira)
    {
        var kobo = decimal.Round(naira * 100m, 0, MidpointRounding.ToEven);
        return new Money((long)kobo);
    }

    public decimal ToNaira() => Kobo / 100m;

    public bool IsZero => Kobo == 0;
    public bool IsPositive => Kobo > 0;
    public bool IsNegative => Kobo < 0;

    public static Money operator +(Money a, Money b) => new(checked(a.Kobo + b.Kobo));
    public static Money operator -(Money a, Money b) => new(checked(a.Kobo - b.Kobo));

    public static bool operator >(Money a, Money b) => a.Kobo > b.Kobo;
    public static bool operator <(Money a, Money b) => a.Kobo < b.Kobo;
    public static bool operator >=(Money a, Money b) => a.Kobo >= b.Kobo;
    public static bool operator <=(Money a, Money b) => a.Kobo <= b.Kobo;

    public int CompareTo(Money other) => Kobo.CompareTo(other.Kobo);

    public override string ToString() => $"NGN {ToNaira():N2}";
}
