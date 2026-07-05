using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Billing;
using Xental.Domain.Common;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>Where to send a refund. All optional — when omitted, the payer's captured source account
/// (from the original deposit webhook) is used.</summary>
public sealed record RefundDestination(string? AccountNumber, string? BankCode, string? AccountName);

public sealed record RefundResult(
    string Status, string TransferRef, long AmountKobo,
    string DestinationAccountNumber, string DestinationBankCode, string? ProviderReference);

/// <summary>
/// Human-approved refund of an overpayment surplus back to the payer. Only the amount that is still
/// held (unsettled + un-refunded) can be sent, so a refund never double-spends against settlement, and
/// on a billing account it draws the surplus out of the carried-forward credit instead of pre-paying
/// the next cycle. Guarded by the global payout kill-switch and idempotent per deposit.
/// </summary>
public sealed class RefundService(
    IApplicationDbContext db, ITenantContext tenantContext, INombaClient nomba, IPayoutSwitch payouts, IClock clock)
{
    public async Task<RefundResult> RefundOverpaymentAsync(string transactionRef, RefundDestination? destination, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        if (!payouts.PayoutsEnabled)
            throw new ValidationException("Payouts are currently paused; refunds cannot be sent right now.");

        var txnRef = (transactionRef ?? string.Empty).Trim();
        var txn = await db.Transactions.FirstOrDefaultAsync(t => t.NombaReference == txnRef && t.TenantId == tenantId, ct)
            ?? throw new NotFoundException($"No deposit found for reference '{txnRef}'.");
        if (txn.VirtualAccountId is not Guid vaId)
            throw new ValidationException("This deposit is not linked to an account.");

        // The account is loaded through the tenant query filter, so this can only touch our own account.
        var account = await db.VirtualAccounts.FirstOrDefaultAsync(v => v.Id == vaId, ct)
            ?? throw new NotFoundException("Account not found.");

        // Refundable surplus: the carried-forward credit on a billing account, otherwise the account's
        // overpayment credit (amount received beyond a fixed expected amount).
        var schedule = await db.BillingSchedules
            .FirstOrDefaultAsync(s => s.VirtualAccountId == vaId && s.Status == BillingScheduleStatus.Active, ct);
        var refundable = schedule?.CarryCreditKobo ?? account.OverpaymentCredit.Kobo;
        if (refundable <= 0)
            throw new ValidationException("This account has no overpayment available to refund.");

        // Cap by what is still held for the account: net collected, less what has already been settled
        // out or refunded. If the surplus already settled to you, there is nothing here to send back.
        var credited = await db.Transactions
            .Where(t => t.VirtualAccountId == vaId && t.Reconciliation != ReconciliationStatus.Reversed)
            .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
        var reversed = await db.Transactions
            .Where(t => t.VirtualAccountId == vaId && t.Reconciliation == ReconciliationStatus.Reversed)
            .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
        var refundPrefix = $"refund-{vaId:N}-";
        var alreadyRefunded = await db.Transfers
            .Where(t => t.MerchantTxRef.StartsWith(refundPrefix) && t.Status != TransferStatus.Failed)
            .SumAsync(t => (long?)t.AmountKobo, ct) ?? 0;
        var available = credited - reversed - account.SettledUpToKobo - alreadyRefunded;

        var amount = Math.Min(refundable, available);
        if (amount <= 0)
            throw new ValidationException("Nothing available to refund — the funds have already been settled or refunded.");

        var (destAccount, destBank, destName) = await ResolveDestinationAsync(destination, txn, ct);

        var merchantRef = $"refund-{vaId:N}-{txn.Id:N}";
        var existing = await db.Transfers.FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef, ct);
        Transfer transfer;
        if (existing is not null)
        {
            if (existing.Status == TransferStatus.Success)
                return Done("already_refunded", existing, destBank);
            if (existing.Status == TransferStatus.Pending)
                throw new ConflictException("A refund for this deposit is already in progress.");
            existing.BeginRetry(); // Failed — allow a fresh attempt
            transfer = existing;
        }
        else
        {
            transfer = new Transfer(tenantId, merchantRef, Money.FromKobo(amount),
                destAccount, destBank, destName, $"Xental refund for {account.Reference}");
            db.Transfers.Add(transfer);
        }
        await db.SaveChangesAsync(ct); // reserve the ref before calling the provider

        var result = await nomba.InitiateTransferAsync(
            merchantRef, transfer.AmountKobo, destAccount, destBank, destName, transfer.Narration, ct);
        if (!result.Success)
        {
            transfer.MarkFailed(result.FailureReason ?? "refund failed", clock.UtcNow);
            await db.SaveChangesAsync(ct);
            throw new ValidationException($"Refund could not be sent: {result.FailureReason}");
        }

        transfer.MarkSucceeded(result.ProviderReference ?? merchantRef, clock.UtcNow);
        // Billing: draw the refund out of the carried credit so it doesn't pre-pay the next cycle.
        schedule?.ReduceCarry(transfer.AmountKobo);
        // Release any active overpayment hold so the legitimate remainder can settle.
        var hold = await db.EscrowHolds.FirstOrDefaultAsync(e => e.VirtualAccountId == vaId && e.State == EscrowState.Held, ct);
        hold?.Release(clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return Done("refunded", transfer, destBank);
    }

    private async Task<(string Account, string Bank, string Name)> ResolveDestinationAsync(
        RefundDestination? destination, Transaction txn, CancellationToken ct)
    {
        var account = !string.IsNullOrWhiteSpace(destination?.AccountNumber) ? destination!.AccountNumber!.Trim()
            : txn.SenderAccountNumber;
        var bank = !string.IsNullOrWhiteSpace(destination?.BankCode) ? destination!.BankCode!.Trim()
            : txn.SenderBankCode;
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(bank))
            throw new ValidationException(
                "Refund destination required — the payer's account wasn't captured on the deposit. Supply accountNumber and bankCode.");

        var name = destination?.AccountName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            // Live payouts need a beneficiary name; resolve it (best-effort) from the payer name or a lookup.
            name = txn.TransferName;
            if (string.IsNullOrWhiteSpace(name))
            {
                try { name = (await nomba.LookupBankAccountAsync(account!, bank!, ct)).AccountName; }
                catch { /* name resolution is best-effort */ }
            }
        }
        return (account!, bank!, name ?? string.Empty);
    }

    private static RefundResult Done(string status, Transfer transfer, string destBank) =>
        new(status, transfer.MerchantTxRef, transfer.AmountKobo,
            transfer.RecipientAccountNumber, destBank, transfer.ProviderReference);
}
