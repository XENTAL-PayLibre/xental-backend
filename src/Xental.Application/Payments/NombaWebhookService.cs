using Microsoft.EntityFrameworkCore;
using Xental.Application.Billing;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Webhooks;
using Xental.Domain.Common;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

public enum WebhookStatus { Processed, Duplicate, Ignored, Review, Reversed }

public sealed record WebhookResult(
    WebhookStatus Status,
    string? Reference = null,
    ReconciliationStatus? Reconciliation = null,
    PaymentState? PaymentState = null,
    TransactionFlag? Reason = null);

/// <summary>
/// Inbound Nomba webhook receiver + reconciliation engine (the core of Xental), implementing
/// the reconciliation rule book: verify HMAC signature, dedupe by reference, match the credited
/// NUBAN, and record an immutable <see cref="Transaction"/> with a visible status + internal
/// reason flag. Inflows are always credited (exact/under/over), never rejected; unknown accounts
/// go to the review queue; reversals reverse the credit. Idempotent, single transaction.
/// </summary>
public sealed class NombaWebhookService(
    IApplicationDbContext db,
    INombaSignatureVerifier signatures,
    RiskEvaluator risk,
    OutboundEventPublisher outbound,
    IReconciliationNotifier notifier,
    RuleEngine rules,
    BillingService billing,
    IClock clock)
{
    public async Task<WebhookResult> ProcessAsync(byte[] rawBody, string? signatureHeader, string? timestampHeader, CancellationToken ct = default)
    {
        if (!signatures.Verify(rawBody, signatureHeader, timestampHeader))
            throw new AuthenticationException("Invalid webhook signature.");

        if (!NombaWebhookParser.TryParse(rawBody, out var inflow))
            return new WebhookResult(WebhookStatus.Ignored);

        return await ReconcileAsync(inflow, ct);
    }

    /// <summary>
    /// Core reconciliation (post-signature, post-parse): dedupe, match the NUBAN, credit + classify,
    /// publish events, and evaluate money rules. The live webhook path and the sandbox simulator
    /// both call this, so a simulated deposit is reconciled by the <b>exact same code</b> as a real one.
    /// </summary>
    public async Task<WebhookResult> ReconcileAsync(NombaInflow inflow, CancellationToken ct = default)
    {
        // Idempotency: same reference already recorded => duplicate, no re-credit (rule book).
        if (await db.Transactions.AnyAsync(t => t.NombaReference == inflow.Reference, ct))
            return new WebhookResult(WebhookStatus.Duplicate);

        var now = clock.UtcNow;
        var occurred = inflow.OccurredAtUtc == DateTimeOffset.UnixEpoch ? now : inflow.OccurredAtUtc;
        var gross = Money.FromKobo(inflow.AmountKobo);
        var fee = Money.FromKobo(inflow.FeeKobo);

        var account = string.IsNullOrWhiteSpace(inflow.AccountNumber) ? null
            : await db.VirtualAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.AccountNumber == inflow.AccountNumber, ct);

        // Reversal: reverse a previously-credited inflow.
        if (inflow.IsReversal)
        {
            account?.ReverseInflow(gross);
            db.Transactions.Add(new Transaction(
                account?.TenantId, account?.Id, inflow.Reference, inflow.TransferName,
                gross, fee, TransactionStatus.Failed, ReconciliationStatus.Reversed,
                TransactionFlag.Reversed, occurred, now));
            await db.SaveChangesAsync(ct);
            if (account is not null) NotifyStatus(account, ReconciliationStatus.Reversed);
            return new WebhookResult(WebhookStatus.Reversed, inflow.Reference, ReconciliationStatus.Reversed, account?.PaymentState, TransactionFlag.Reversed);
        }

        // Unknown account => review queue (rule book), no credit.
        if (account is null)
        {
            db.Transactions.Add(new Transaction(
                null, null, inflow.Reference, inflow.TransferName,
                gross, fee, TransactionStatus.Success, ReconciliationStatus.PendingReview,
                TransactionFlag.InvalidAccount, occurred, null));
            await db.SaveChangesAsync(ct);
            return new WebhookResult(WebhookStatus.Review, inflow.Reference, ReconciliationStatus.PendingReview, Reason: TransactionFlag.InvalidAccount);
        }

        // Reconcile against the expected amount (always credits; classifies the result).
        var reconciliation = account.ApplyInflow(gross);

        // Name-mismatch flag (third-party deposit / typo). Amount discrepancy takes precedence.
        var customer = await db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == account.CustomerId, ct);
        var nameMismatch = NameMismatch(inflow.TransferName, customer?.Name);

        var reason = reconciliation switch
        {
            ReconciliationStatus.Underpaid => TransactionFlag.Underpaid,
            ReconciliationStatus.Overpaid => TransactionFlag.Overpaid,
            _ => nameMismatch ? TransactionFlag.NameMismatch : (TransactionFlag?)null,
        };

        // Risk scoring — a high score routes to review even when the amount reconciles.
        var riskScore = await risk.ScoreAsync(account, inflow, nameMismatch, ct);
        if (riskScore >= RiskEvaluator.ReviewThreshold)
        {
            reconciliation = ReconciliationStatus.PendingReview;
            reason = TransactionFlag.ManualReview;
        }

        var txn = new Transaction(
            account.TenantId, account.Id, inflow.Reference, inflow.TransferName,
            gross, fee, TransactionStatus.Success, reconciliation, reason, occurred, now, riskScore);
        db.Transactions.Add(txn);

        // Enqueue enriched outbound events (delivered async by the worker), in the same tx.
        await outbound.PublishDepositAsync(account, txn, ct);

        await db.SaveChangesAsync(ct);

        // Live Checkout: push the new status to any open subscribers. Best-effort, post-commit,
        // and fully isolated — a notifier failure can never affect the reconciliation outcome.
        NotifyStatus(account, reconciliation);

        // Money Rules (Feature 3): react to the committed outcome. Post-commit + isolated, so a
        // rule failure cannot corrupt the reconciliation that already succeeded.
        try { await rules.EvaluateAsync(account, txn, ct); }
        catch { /* rules are advisory — never fail the webhook over them */ }

        // Recurring billing (push model): attribute this credit to the account's schedule periods.
        // Post-commit + isolated + idempotent (water-mark) — never fails the webhook over billing.
        try { await billing.AttributeDepositAsync(account.Id, ct); }
        catch { /* billing attribution is advisory to the money path */ }

        return new WebhookResult(WebhookStatus.Processed, account.Reference, reconciliation, account.PaymentState, reason);
    }

    private void NotifyStatus(VirtualAccount account, ReconciliationStatus reconciliation)
    {
        try
        {
            notifier.Publish(new CheckoutStatusEvent(
                account.Id, account.Reference, account.PaymentState.ToString(),
                account.AmountPaidKobo, account.ExpectedAmountKobo, reconciliation.ToString()));
        }
        catch { /* pure notify path — swallow so it can't touch the money path */ }
    }

    private static bool NameMismatch(string? transferName, string? customerName)
    {
        if (string.IsNullOrWhiteSpace(transferName) || string.IsNullOrWhiteSpace(customerName))
            return false;
        var a = transferName.Trim().ToLowerInvariant();
        var b = customerName.Trim().ToLowerInvariant();
        return a != b && !a.Contains(b) && !b.Contains(a);
    }
}
