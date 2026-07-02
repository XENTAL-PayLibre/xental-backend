using Xental.Domain.Common;

namespace Xental.Domain.Payments;

public enum TransferStatus { Pending = 1, Success = 2, Failed = 3 }

/// <summary>
/// An outbound bank transfer (payout/settlement). Idempotent on the developer-supplied
/// <see cref="MerchantTxRef"/> (unique per tenant): re-submitting the same ref returns the
/// existing transfer instead of moving money twice. Amounts are integer kobo.
/// </summary>
public sealed class Transfer : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string MerchantTxRef { get; private set; } = null!;
    public long AmountKobo { get; private set; }
    public string RecipientAccountNumber { get; private set; } = null!;
    public string RecipientBankCode { get; private set; } = null!;
    public string? RecipientName { get; private set; }
    public string? Narration { get; private set; }
    public TransferStatus Status { get; private set; }
    public string? ProviderReference { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private Transfer() { } // EF

    public Transfer(
        Guid tenantId, string merchantTxRef, Money amount,
        string recipientAccountNumber, string recipientBankCode, string? recipientName, string? narration)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (!amount.IsPositive) throw new DomainException("Transfer amount must be positive.");
        TenantId = tenantId;
        MerchantTxRef = DomainException.Require(merchantTxRef, nameof(merchantTxRef));
        AmountKobo = amount.Kobo;
        RecipientAccountNumber = DomainException.Require(recipientAccountNumber, nameof(recipientAccountNumber));
        RecipientBankCode = DomainException.Require(recipientBankCode, nameof(recipientBankCode));
        RecipientName = recipientName;
        Narration = narration;
        Status = TransferStatus.Pending;
    }

    public Money Amount => Money.FromKobo(AmountKobo);

    public void MarkSucceeded(string providerReference, DateTimeOffset at)
    {
        Status = TransferStatus.Success;
        ProviderReference = providerReference;
        CompletedAtUtc = at;
    }

    public void MarkFailed(string reason, DateTimeOffset at)
    {
        Status = TransferStatus.Failed;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
        CompletedAtUtc = at;
    }
}
