using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>Customer-visible deposit status.</summary>
public enum TransactionStatus { Pending = 1, Success = 2, Failed = 3 }

/// <summary>How a deposit reconciled (per the reconciliation rule book).</summary>
public enum ReconciliationStatus { Reconciled = 1, Underpaid = 2, Overpaid = 3, PendingReview = 4, Failed = 5, Reversed = 6 }

/// <summary>Internal flag explaining an exception (stored, not customer-facing).</summary>
public enum TransactionFlag { NameMismatch = 1, Underpaid = 2, Overpaid = 3, Reversed = 4, InvalidAccount = 5, Duplicate = 6, ManualReview = 7 }

/// <summary>
/// An immutable deposit record against a dedicated (virtual) account. Doubles as the
/// idempotency guard: <see cref="NombaReference"/> is globally unique, so a replayed webhook
/// is ignored. Unknown-account deposits are stored with a null account for the review queue.
/// Amounts are integer kobo; the net credit is the amount less provider fees.
/// </summary>
public sealed class Transaction : BaseEntity
{
    public Guid? TenantId { get; private set; }              // null when the account is unknown (review queue)
    public Guid? VirtualAccountId { get; private set; }      // dedicated_account_id
    public string NombaReference { get; private set; } = null!;
    public string? TransferName { get; private set; }        // original name on the transfer
    public string? SenderAccountNumber { get; private set; } // payer's source account (pre-fills overpayment refunds)
    public string? SenderBankCode { get; private set; }
    public long AmountKobo { get; private set; }             // gross received (what the payer sent)
    public long FeeKobo { get; private set; }
    public long NetCreditKobo { get; private set; }          // credited to the merchant = amount - fee
    public TransactionStatus Status { get; private set; }
    public ReconciliationStatus Reconciliation { get; private set; }
    public TransactionFlag? Reason { get; private set; }
    /// <summary>0–100 risk score computed at ingestion (velocity, name reuse, large overpayment).</summary>
    public int RiskScore { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public DateTimeOffset? ReconciledAtUtc { get; private set; }

    private Transaction() { } // EF

    public Transaction(
        Guid? tenantId, Guid? virtualAccountId, string nombaReference, string? transferName,
        Money amount, Money fee, TransactionStatus status, ReconciliationStatus reconciliation,
        TransactionFlag? reason, DateTimeOffset occurredAtUtc, DateTimeOffset? reconciledAtUtc, int riskScore = 0,
        string? senderAccountNumber = null, string? senderBankCode = null)
    {
        NombaReference = DomainException.Require(nombaReference, nameof(nombaReference));
        TenantId = tenantId;
        VirtualAccountId = virtualAccountId;
        TransferName = string.IsNullOrWhiteSpace(transferName) ? null : transferName.Trim();
        SenderAccountNumber = string.IsNullOrWhiteSpace(senderAccountNumber) ? null : senderAccountNumber.Trim();
        SenderBankCode = string.IsNullOrWhiteSpace(senderBankCode) ? null : senderBankCode.Trim();
        AmountKobo = amount.Kobo;
        FeeKobo = fee.Kobo;
        NetCreditKobo = Math.Max(0, amount.Kobo - fee.Kobo);
        Status = status;
        Reconciliation = reconciliation;
        Reason = reason;
        RiskScore = Math.Clamp(riskScore, 0, 100);
        OccurredAtUtc = occurredAtUtc;
        ReconciledAtUtc = reconciledAtUtc;
    }

    public Money Amount => Money.FromKobo(AmountKobo);
    public Money NetCredit => Money.FromKobo(NetCreditKobo);
}
