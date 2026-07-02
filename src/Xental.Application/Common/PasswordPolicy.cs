using Xental.Application.Common.Exceptions;

namespace Xental.Application.Common;

/// <summary>
/// Strong-password rules enforced on registration and password reset. A password
/// must be 12–128 chars and contain an uppercase letter, a lowercase letter, a
/// digit, and a non-alphanumeric character.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public static void Validate(string? password)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            errors.Add($"be at least {MinLength} characters");
        if (password is { Length: > MaxLength })
            errors.Add($"be at most {MaxLength} characters");
        if (password is null || !password.Any(char.IsUpper))
            errors.Add("contain an uppercase letter");
        if (password is null || !password.Any(char.IsLower))
            errors.Add("contain a lowercase letter");
        if (password is null || !password.Any(char.IsDigit))
            errors.Add("contain a number");
        if (password is null || !password.Any(c => !char.IsLetterOrDigit(c)))
            errors.Add("contain a special character");

        if (errors.Count > 0)
            throw new ValidationException("Password must " + string.Join(", ", errors) + ".");
    }
}
