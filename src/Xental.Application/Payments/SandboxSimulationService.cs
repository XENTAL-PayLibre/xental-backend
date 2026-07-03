using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Application.Payments;

/// <summary>
/// Sandbox deposit simulator (agent layer). Drives a synthetic inflow through the <b>real</b>
/// reconciliation path with zero money and no call to Nomba — so a developer (or their agent) can
/// build and verify an integration end-to-end: create account → simulate payment → observe
/// reconciliation → configure splits/rules → re-simulate. Test-mode only (enforced at the API edge).
/// </summary>
public sealed class SandboxSimulationService(IApplicationDbContext db, ITenantContext tenantContext, NombaWebhookService webhook, IClock clock)
{
    public async Task<WebhookResult> SimulateDepositAsync(string accountRef, long amountKobo, string? senderName, bool reversal, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        if (amountKobo <= 0)
            throw new ValidationException("Simulated amount must be positive.");

        var account = await db.VirtualAccounts.FirstOrDefaultAsync(v => v.Reference == accountRef, ct)
            ?? throw new NotFoundException($"Virtual account '{accountRef}' not found.");

        var inflow = new NombaInflow(
            Reference: "sim-" + Base64Url(RandomNumberGenerator.GetBytes(12)),
            AccountNumber: account.AccountNumber,
            AmountKobo: amountKobo,
            FeeKobo: 0,
            TransferName: senderName ?? account.AccountName,
            EventType: reversal ? "payment_reversal" : "payment_success",
            OccurredAtUtc: clock.UtcNow);

        return await webhook.ReconcileAsync(inflow, ct);
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
