using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Application.Webhooks;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>
/// The Money Rules engine (Feature 3). Runs <b>after</b> the reconciliation transaction commits, so
/// it reacts to the outcome and can never change the verdict. Each action reuses an existing
/// primitive — Hold → an escrow hold (Feature 1), Notify → an outbound webhook event, ReviewFlag →
/// an audit-style event. Actions are idempotent and isolated: a rule failure can never corrupt
/// reconciliation. No rules configured → a no-op.
/// </summary>
public sealed class RuleEngine(IApplicationDbContext db, OutboundEventPublisher outbound, IClock clock)
{
    public async Task EvaluateAsync(VirtualAccount account, Transaction txn, CancellationToken ct = default)
    {
        var rules = await db.MoneyRules.IgnoreQueryFilters()
            .Where(r => r.TenantId == account.TenantId && r.Enabled)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);
        if (rules.Count == 0)
            return;

        var overpayment = account.OverpaymentCredit.Kobo;
        var deficit = account.Deficit.Kobo;
        var changed = false;

        foreach (var rule in rules)
        {
            if (!rule.Matches(txn.Reconciliation, account.PaymentState, overpayment, deficit, txn.RiskScore))
                continue;

            switch (rule.Action)
            {
                case RuleAction.Hold:
                    // Idempotent: only one active hold per account.
                    var active = await db.EscrowHolds.IgnoreQueryFilters()
                        .AnyAsync(e => e.VirtualAccountId == account.Id && e.State == EscrowState.Held, ct);
                    if (!active)
                    {
                        db.EscrowHolds.Add(new EscrowHold(account.TenantId, account.Id, account.AmountPaidKobo,
                            $"money-rule:{rule.Trigger}"));
                        changed = true;
                    }
                    break;

                case RuleAction.Notify:
                case RuleAction.ReviewFlag:
                    await outbound.PublishEventAsync(account.TenantId,
                        rule.Action == RuleAction.Notify ? "rule.notify" : "rule.review_flag",
                        new
                        {
                            accountRef = account.Reference,
                            transactionRef = txn.NombaReference,
                            trigger = rule.Trigger.ToString(),
                            action = rule.Action.ToString(),
                            paymentState = account.PaymentState.ToString(),
                            reconciliation = txn.Reconciliation.ToString(),
                            riskScore = txn.RiskScore,
                            overpaymentKobo = overpayment,
                            deficitKobo = deficit,
                        }, ct);
                    changed = true;
                    break;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }
}
