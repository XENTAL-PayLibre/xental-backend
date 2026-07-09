using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Application.Webhooks;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>
/// Runs programmable payment flows after a deposit's reconciliation commits. For each enabled flow
/// whose trigger + conditions match, every action is executed in order, isolated (a failing action
/// never corrupts reconciliation or aborts the others), and a <see cref="FlowRun"/> audit row is
/// written. No flows configured → a no-op. This is the multi-step evolution of the Money Rules engine.
/// </summary>
public sealed class FlowEngine(IApplicationDbContext db, OutboundEventPublisher outbound, IClock clock)
{
    public async Task RunAsync(VirtualAccount account, Transaction txn, CancellationToken ct = default)
    {
        var flows = await db.Flows.IgnoreQueryFilters()
            .Include(f => f.Actions)
            .Where(f => f.TenantId == account.TenantId && f.Enabled)
            .OrderBy(f => f.Priority)
            .ToListAsync(ct);
        if (flows.Count == 0)
            return;

        var overpayment = account.OverpaymentCredit.Kobo;
        var deficit = account.Deficit.Kobo;
        var changed = false;

        foreach (var flow in flows)
        {
            if (!flow.Matches(txn.Reconciliation, account.PaymentState, overpayment, deficit, txn.RiskScore, txn.AmountKobo))
                continue;

            var outcomes = new List<string>();
            foreach (var action in flow.Actions.OrderBy(a => a.Order))
            {
                try
                {
                    outcomes.Add(await ExecuteAsync(action.Type, flow, account, txn, overpayment, deficit, ct));
                    changed = true;
                }
                catch (Exception ex)
                {
                    outcomes.Add($"{action.Type}: failed ({ex.Message})");
                }
            }

            db.FlowRuns.Add(new FlowRun(
                account.TenantId, flow.Id, flow.Name, flow.Trigger.ToString(),
                account.Reference, txn.NombaReference,
                outcomes.Count == 0 ? "matched (no actions)" : string.Join("; ", outcomes),
                clock.UtcNow));
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    private async Task<string> ExecuteAsync(
        FlowActionType type, Flow flow, VirtualAccount account, Transaction txn, long overpayment, long deficit, CancellationToken ct)
    {
        switch (type)
        {
            case FlowActionType.Hold:
            {
                var active = await db.EscrowHolds.IgnoreQueryFilters()
                    .AnyAsync(e => e.VirtualAccountId == account.Id && e.State == EscrowState.Held, ct);
                if (active) return "Hold: already held";
                db.EscrowHolds.Add(new EscrowHold(account.TenantId, account.Id, account.AmountPaidKobo, $"flow:{flow.Name}"));
                return "Hold: escrow hold placed";
            }
            case FlowActionType.Release:
            {
                var hold = await db.EscrowHolds.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(e => e.VirtualAccountId == account.Id && e.State == EscrowState.Held, ct);
                if (hold is null) return "Release: no active hold";
                hold.Release(clock.UtcNow);
                return "Release: escrow hold released";
            }
            case FlowActionType.NotifyWebhook:
            case FlowActionType.ReviewFlag:
            {
                var eventType = type == FlowActionType.NotifyWebhook ? "flow.notify" : "flow.review_flag";
                await outbound.PublishEventAsync(account.TenantId, eventType, new
                {
                    flow = flow.Name,
                    trigger = flow.Trigger.ToString(),
                    accountRef = account.Reference,
                    transactionRef = txn.NombaReference,
                    paymentState = account.PaymentState.ToString(),
                    reconciliation = txn.Reconciliation.ToString(),
                    amountKobo = txn.AmountKobo,
                    overpaymentKobo = overpayment,
                    deficitKobo = deficit,
                    riskScore = txn.RiskScore,
                }, ct);
                return $"{type}: event {eventType} published";
            }
            default:
                return $"{type}: unsupported";
        }
    }
}
