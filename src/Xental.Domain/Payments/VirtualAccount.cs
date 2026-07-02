using Xental.Domain.Common;

namespace Xental.Domain.Payments;

public enum VirtualAccountStatus { Active = 1, Closed = 2 }

/// <summary>Cumulative payment progress against a virtual account's expected amount.</summary>
public enum PaymentState { Unpaid = 1, PartiallyPaid = 2, FullyPaid = 3, Overpaid = 4 }

/// <summary>
/// A persistent NUBAN (bank account number) mapped to a <see cref="Customer"/>, provisioned
/// via Nomba. Optionally carries an <c>expected_amount</c> (kobo) used to reconcile inflows:
/// exact → FULLY_PAID, short → PARTIALLY_PAID (deficit), over → OVERPAID (rolling credit).
/// All money is integer kobo.
/// </summary>
public sealed class VirtualAccount : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string Reference { get; private set; } = null!;      // accountRef (unique per tenant)

    public string AccountNumber { get; private set; } = null!;  // NUBAN
    public string BankName { get; private set; } = null!;
    public string AccountName { get; private set; } = null!;
    public string? ProviderAccountId { get; private set; }      // Nomba's id/order ref for this VA

    public long? ExpectedAmountKobo { get; private set; }
    public long AmountPaidKobo { get; private set; }
    public DateTimeOffset? ExpiryDateUtc { get; private set; }

    public VirtualAccountStatus Status { get; private set; }
    public PaymentState PaymentState { get; private set; }
    /// <summary>Set once collected funds have been swept to the tenant's settlement account.</summary>
    public DateTimeOffset? SettledAtUtc { get; private set; }

    private VirtualAccount() { } // EF

    public VirtualAccount(
        Guid tenantId, Guid customerId, string reference,
        string accountNumber, string bankName, string accountName,
        string? providerAccountId = null, long? expectedAmountKobo = null, DateTimeOffset? expiryDateUtc = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (customerId == Guid.Empty) throw new DomainException("CustomerId is required.");
        if (expectedAmountKobo is < 0) throw new DomainException("Expected amount cannot be negative.");
        TenantId = tenantId;
        CustomerId = customerId;
        Reference = DomainException.Require(reference, nameof(reference));
        AccountNumber = DomainException.Require(accountNumber, nameof(accountNumber));
        BankName = DomainException.Require(bankName, nameof(bankName));
        AccountName = DomainException.Require(accountName, nameof(accountName));
        ProviderAccountId = providerAccountId;
        ExpectedAmountKobo = expectedAmountKobo;
        ExpiryDateUtc = expiryDateUtc;
        Status = VirtualAccountStatus.Active;
        PaymentState = PaymentState.Unpaid;
    }

    public Money AmountPaid => Money.FromKobo(AmountPaidKobo);
    public Money? ExpectedAmount => ExpectedAmountKobo is long k ? Money.FromKobo(k) : null;

    /// <summary>Remaining amount owed when short-paid (kobo); zero once met/exceeded or no expectation.</summary>
    public Money Deficit => ExpectedAmountKobo is long e && AmountPaidKobo < e ? Money.FromKobo(e - AmountPaidKobo) : Money.Zero;

    /// <summary>Amount paid beyond the expectation (kobo); zero otherwise.</summary>
    public Money OverpaymentCredit => ExpectedAmountKobo is long e && AmountPaidKobo > e ? Money.FromKobo(AmountPaidKobo - e) : Money.Zero;

    /// <summary>
    /// Apply an inflow (gross, i.e. what the payer sent) to the running total and reconcile
    /// against the expected amount. Never rejects — an inflow is always credited; the return
    /// value classifies it (Reconciled / Underpaid / Overpaid).
    /// </summary>
    public ReconciliationStatus ApplyInflow(Money amount)
    {
        if (!amount.IsPositive)
            throw new DomainException("Inflow amount must be positive.");

        AmountPaidKobo = checked(AmountPaidKobo + amount.Kobo);

        if (ExpectedAmountKobo is not long expected)
            return ReconciliationStatus.Reconciled; // open account, no expectation to match

        if (AmountPaidKobo == expected) { PaymentState = PaymentState.FullyPaid; return ReconciliationStatus.Reconciled; }
        if (AmountPaidKobo < expected) { PaymentState = PaymentState.PartiallyPaid; return ReconciliationStatus.Underpaid; }
        PaymentState = PaymentState.Overpaid; return ReconciliationStatus.Overpaid;
    }

    /// <summary>Reverse a previously-credited inflow (bank reversal). Never goes below zero.</summary>
    public void ReverseInflow(Money amount)
    {
        AmountPaidKobo = Math.Max(0, AmountPaidKobo - amount.Kobo);
        if (ExpectedAmountKobo is long expected)
            PaymentState = AmountPaidKobo == 0 ? PaymentState.Unpaid
                : AmountPaidKobo < expected ? PaymentState.PartiallyPaid
                : AmountPaidKobo == expected ? PaymentState.FullyPaid
                : PaymentState.Overpaid;
    }

    public bool IsSettled => SettledAtUtc is not null;
    public void MarkSettled(DateTimeOffset at) => SettledAtUtc = at;

    public void Close() => Status = VirtualAccountStatus.Closed;
}
