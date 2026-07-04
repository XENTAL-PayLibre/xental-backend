using Xental.Domain.Common;

namespace Xental.Domain.Tenancy;

public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>
/// A developer account — the security + isolation boundary. Signs in to the
/// dashboard with email + password (or a linked social login) and owns API keys
/// and sub-merchants. The password is stored only as a bcrypt hash; it is null
/// for accounts that only use social login.
/// </summary>
public sealed class Tenant : BaseEntity
{
    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? PasswordHash { get; private set; }
    public bool EmailVerified { get; private set; }
    public TenantStatus Status { get; private set; }

    private Tenant() { } // EF

    public Tenant(string name, string email, string? passwordHash)
    {
        Name = DomainException.Require(name, nameof(name));
        Email = NormalizeEmail(email);
        PasswordHash = passwordHash;
        EmailVerified = false;
        Status = TenantStatus.Active;
    }

    public bool IsActive => Status == TenantStatus.Active;
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    public void SetPassword(string passwordHash) =>
        PasswordHash = DomainException.Require(passwordHash, nameof(passwordHash));

    public void MarkEmailVerified() => EmailVerified = true;

    public void Suspend() => Status = TenantStatus.Suspended;
    public void Reactivate() => Status = TenantStatus.Active;

    /// <summary>Emails are compared case-insensitively; store them normalized.</summary>
    public static string NormalizeEmail(string email) =>
        DomainException.Require(email, nameof(email)).Trim().ToLowerInvariant();
}
