using Xental.Domain.Common;

namespace Xental.Domain.Payments;

public enum EscrowState { Held = 1, Released = 2, Cancelled = 3 }

/// <summary>
/// A hold placed on a virtual account's collected funds so the settlement worker will not sweep
/// (or split) them until the hold is released. Additive and opt-in: an account with no active hold
/// settles exactly as today. Releasing flips the state and lets the next sweep proceed.
/// </summary>
public sealed class EscrowHold : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid VirtualAccountId { get; private set; }
    public long AmountKobo { get; private set; }
    public EscrowState State { get; private set; }
    public string? ReleaseCondition { get; private set; }
    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    private EscrowHold() { } // EF

    public EscrowHold(Guid tenantId, Guid virtualAccountId, long amountKobo, string? releaseCondition)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (virtualAccountId == Guid.Empty) throw new DomainException("VirtualAccountId is required.");
        if (amountKobo < 0) throw new DomainException("Escrow amount cannot be negative.");
        TenantId = tenantId;
        VirtualAccountId = virtualAccountId;
        AmountKobo = amountKobo;
        ReleaseCondition = releaseCondition;
        State = EscrowState.Held;
    }

    public bool IsActive => State == EscrowState.Held;

    public void Release(DateTimeOffset at)
    {
        if (State != EscrowState.Held) throw new DomainException("Only a held escrow can be released.");
        State = EscrowState.Released;
        ReleasedAtUtc = at;
    }

    public void Cancel(DateTimeOffset at)
    {
        if (State != EscrowState.Held) throw new DomainException("Only a held escrow can be cancelled.");
        State = EscrowState.Cancelled;
        ReleasedAtUtc = at;
    }
}
