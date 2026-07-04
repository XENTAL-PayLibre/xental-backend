namespace Xental.Domain.Common;

/// <summary>Raised when a domain invariant is violated.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }

    public static string Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{field} is required.");
        return value.Trim();
    }
}
