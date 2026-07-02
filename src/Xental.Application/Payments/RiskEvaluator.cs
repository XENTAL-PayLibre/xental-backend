using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>
/// Lightweight, explainable risk scoring for inbound deposits (0–100). Combines signals that
/// matter for Nigerian bank transfers: name mismatch, large overpayment, deposit velocity on a
/// single account, and transfer-name reuse across many accounts (a mule/structuring pattern).
/// High scores route a deposit to the review queue even when the amount reconciles.
/// </summary>
public sealed class RiskEvaluator(IApplicationDbContext db, IClock clock)
{
    public const int ReviewThreshold = 70;

    public async Task<int> ScoreAsync(VirtualAccount account, NombaInflow inflow, bool nameMismatch, CancellationToken ct = default)
    {
        var score = 0;
        var now = clock.UtcNow;

        if (nameMismatch)
            score += 30;

        // Large overpayment (> 50% over expected) is unusual and worth a look.
        if (account.ExpectedAmountKobo is long expected && expected > 0 && inflow.AmountKobo > expected + expected / 2)
            score += 25;

        // Velocity: several deposits to the same account within 10 minutes.
        var since = now.AddMinutes(-10);
        var recent = await db.Transactions.AsNoTracking()
            .Where(t => t.VirtualAccountId == account.Id && t.CreatedAtUtc >= since)
            .CountAsync(ct);
        if (recent >= 3) score += 25;
        else if (recent >= 1) score += 10;

        // Name reuse: same payer name hitting multiple distinct accounts in 24h (mule pattern).
        if (!string.IsNullOrWhiteSpace(inflow.TransferName))
        {
            var day = now.AddHours(-24);
            var name = inflow.TransferName.Trim();
            var distinctAccounts = await db.Transactions.AsNoTracking()
                .Where(t => t.TransferName == name && t.CreatedAtUtc >= day && t.VirtualAccountId != null)
                .Select(t => t.VirtualAccountId)
                .Distinct()
                .CountAsync(ct);
            if (distinctAccounts >= 2) score += 40;
        }

        return Math.Clamp(score, 0, 100);
    }
}
