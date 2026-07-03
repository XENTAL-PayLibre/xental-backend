using Xental.Domain.Common;

namespace Xental.Domain.Tenancy;

/// <summary>Role of a team member within a developer account.</summary>
public enum TeamRole { Admin = 1, Employee = 2, Developer = 3 }

public enum TeamMemberStatus { Invited = 1, Active = 2, Removed = 3 }

/// <summary>
/// A person on a developer account's team. Invited by email; on accepting they set a password and
/// can sign in to the <b>same account</b> with their <see cref="Role"/>. Emails are unique per
/// account among non-removed members. Only a hash of the invite token is stored.
/// </summary>
public sealed class TeamMember : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public TeamRole Role { get; private set; }
    public TeamMemberStatus Status { get; private set; }
    public string? PasswordHash { get; private set; }
    public string? InviteTokenHash { get; private set; }
    public DateTimeOffset? InviteExpiresAtUtc { get; private set; }
    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    private TeamMember() { } // EF

    /// <summary>Create an invited member (no password yet) with a hashed, expiring invite token.</summary>
    public TeamMember(Guid tenantId, string name, string email, TeamRole role, string inviteTokenHash, DateTimeOffset inviteExpiresAtUtc)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Name = DomainException.Require(name, nameof(name));
        Email = NormalizeEmail(email);
        Role = role;
        Status = TeamMemberStatus.Invited;
        InviteTokenHash = DomainException.Require(inviteTokenHash, nameof(inviteTokenHash));
        InviteExpiresAtUtc = inviteExpiresAtUtc;
    }

    public bool IsActive => Status == TeamMemberStatus.Active;
    public bool CanSignIn => Status == TeamMemberStatus.Active && !string.IsNullOrEmpty(PasswordHash);

    public bool InviteUsable(DateTimeOffset now) =>
        Status == TeamMemberStatus.Invited && InviteTokenHash is not null && InviteExpiresAtUtc is DateTimeOffset e && now < e;

    /// <summary>Accept the invite: set the password, activate, and consume the invite token.</summary>
    public void Accept(string passwordHash, DateTimeOffset at)
    {
        PasswordHash = DomainException.Require(passwordHash, nameof(passwordHash));
        Status = TeamMemberStatus.Active;
        AcceptedAtUtc = at;
        InviteTokenHash = null;
        InviteExpiresAtUtc = null;
    }

    /// <summary>Re-issue a fresh invite (e.g. resend) for a still-pending member.</summary>
    public void ReInvite(string inviteTokenHash, DateTimeOffset expiresAtUtc)
    {
        if (Status != TeamMemberStatus.Invited) throw new DomainException("Only a pending member can be re-invited.");
        InviteTokenHash = DomainException.Require(inviteTokenHash, nameof(inviteTokenHash));
        InviteExpiresAtUtc = expiresAtUtc;
    }

    public void Update(string name, string email, TeamRole role)
    {
        Name = DomainException.Require(name, nameof(name));
        Email = NormalizeEmail(email);
        Role = role;
    }

    public void Remove() => Status = TeamMemberStatus.Removed;

    /// <summary>Emails are compared case-insensitively; store them normalized.</summary>
    public static string NormalizeEmail(string email) =>
        DomainException.Require(email, nameof(email)).Trim().ToLowerInvariant();
}
