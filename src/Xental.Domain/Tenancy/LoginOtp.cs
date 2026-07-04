using Xental.Domain.Common;

namespace Xental.Domain.Tenancy;

/// <summary>
/// A one-time passcode emailed as the second step of dashboard login. Issued only after the password
/// has been verified; the user must present the code to receive a session. The code itself is stored
/// only as a hash. Single-use, short-lived, and attempt-limited to make online guessing infeasible.
/// </summary>
public sealed class LoginOtp : BaseEntity, ITenantOwned
{
    public const int MaxAttempts = 5;

    public Guid TenantId { get; private set; }
    /// <summary>Set when the login subject is a team member (not the account owner).</summary>
    public Guid? TeamMemberId { get; private set; }
    public string CodeHash { get; private set; } = null!;
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public bool Consumed { get; private set; }
    public int Attempts { get; private set; }

    private LoginOtp() { } // EF

    public LoginOtp(Guid tenantId, Guid? teamMemberId, string codeHash, DateTimeOffset expiresAtUtc)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        TeamMemberId = teamMemberId;
        CodeHash = DomainException.Require(codeHash, nameof(codeHash));
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;

    /// <summary>Usable if not yet consumed, not expired, and attempts remain.</summary>
    public bool IsRedeemable(DateTimeOffset now) => !Consumed && !IsExpired(now) && Attempts < MaxAttempts;

    /// <summary>Record a wrong-code attempt; the OTP self-invalidates once attempts are exhausted.</summary>
    public void RegisterFailedAttempt()
    {
        Attempts++;
        if (Attempts >= MaxAttempts) Consumed = true;
    }

    public void Consume() => Consumed = true;
}
