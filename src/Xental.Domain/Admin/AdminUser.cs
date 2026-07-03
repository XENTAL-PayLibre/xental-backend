using Xental.Domain.Common;

namespace Xental.Domain.Admin;

/// <summary>Admin privilege level. SuperAdmin can manage other admins; both can review/reconcile.</summary>
public enum AdminRole { Admin = 1, SuperAdmin = 2 }

public enum AdminStatus { Active = 1, Disabled = 2 }

/// <summary>
/// An operator of the admin plane — separate from tenants (developer accounts). Authenticates with
/// email + password and a mandatory TOTP second factor. Not <see cref="ITenantOwned"/>: admins act
/// across tenants, through audited endpoints only.
/// </summary>
public sealed class AdminUser : BaseEntity
{
    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public AdminRole Role { get; private set; }
    public AdminStatus Status { get; private set; }

    /// <summary>Encrypted TOTP shared secret (set at enrollment). Null until MFA is enrolled.</summary>
    public string? TotpSecretEncrypted { get; private set; }
    public bool MfaEnabled { get; private set; }
    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    private AdminUser() { } // EF

    public AdminUser(string email, string passwordHash, AdminRole role)
    {
        Email = NormalizeEmail(email);
        PasswordHash = DomainException.Require(passwordHash, nameof(passwordHash));
        Role = role;
        Status = AdminStatus.Active;
    }

    public bool IsActive => Status == AdminStatus.Active;
    public bool IsSuperAdmin => Role == AdminRole.SuperAdmin;

    public void EnrollMfa(string totpSecretEncrypted)
    {
        TotpSecretEncrypted = DomainException.Require(totpSecretEncrypted, nameof(totpSecretEncrypted));
        MfaEnabled = true;
    }

    public void SetPassword(string passwordHash) => PasswordHash = DomainException.Require(passwordHash, nameof(passwordHash));
    public void ChangeRole(AdminRole role) => Role = role;
    public void Disable() => Status = AdminStatus.Disabled;
    public void Reactivate() => Status = AdminStatus.Active;
    public void MarkLogin(DateTimeOffset at) => LastLoginAtUtc = at;

    public static string NormalizeEmail(string email) =>
        DomainException.Require(email, nameof(email)).Trim().ToLowerInvariant();
}

/// <summary>Append-only record of an admin action (approvals, rejections, reconciliation moves).</summary>
public sealed class AdminAuditLog : BaseEntity
{
    public Guid AdminId { get; private set; }
    public string Action { get; private set; } = null!;
    public string? TargetTenantId { get; private set; }
    public string? Detail { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }

    private AdminAuditLog() { } // EF

    public AdminAuditLog(Guid adminId, string action, string? targetTenantId, string? detail, DateTimeOffset atUtc)
    {
        if (adminId == Guid.Empty) throw new DomainException("AdminId is required.");
        AdminId = adminId;
        Action = DomainException.Require(action, nameof(action));
        TargetTenantId = targetTenantId;
        Detail = detail is { Length: > 500 } ? detail[..500] : detail;
        AtUtc = atUtc;
    }
}
