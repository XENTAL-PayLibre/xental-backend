using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;
using Xental.Domain.Webhooks;

namespace Xental.Application.Webhooks;

/// <summary>
/// Fans a reconciled deposit out to the tenant's active webhook endpoints as an enriched,
/// pre-reconciled event. Creates one <see cref="WebhookDelivery"/> per endpoint (the background
/// worker signs + delivers them). Does not save — it enlists in the caller's transaction so the
/// deposit and its deliveries commit atomically.
/// </summary>
public sealed class OutboundEventPublisher(IApplicationDbContext db, IClock clock)
{
    public async Task PublishDepositAsync(VirtualAccount account, Transaction txn, CancellationToken ct = default)
    {
        var endpoints = await db.WebhookEndpoints
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == account.TenantId && e.Active)
            .ToListAsync(ct);
        if (endpoints.Count == 0)
            return;

        var eventId = txn.Id.ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            id = eventId,
            @event = "deposit.reconciled",
            createdAt = clock.UtcNow,
            data = new
            {
                accountRef = account.Reference,
                accountNumber = account.AccountNumber,
                transactionRef = txn.NombaReference,
                amountKobo = txn.AmountKobo,
                feeKobo = txn.FeeKobo,
                netCreditKobo = txn.NetCreditKobo,
                reconciliation = txn.Reconciliation.ToString(),
                paymentState = account.PaymentState.ToString(),
                reason = txn.Reason?.ToString(),
                riskScore = txn.RiskScore,
                amountPaidKobo = account.AmountPaidKobo,
                expectedAmountKobo = account.ExpectedAmountKobo,
                transferName = txn.TransferName,
                occurredAt = txn.OccurredAtUtc,
            },
        });

        foreach (var endpoint in endpoints)
            db.WebhookDeliveries.Add(new WebhookDelivery(
                account.TenantId, endpoint.Id, eventId, "deposit.reconciled", payload, clock.UtcNow));
    }
}
