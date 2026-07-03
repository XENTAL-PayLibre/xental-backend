using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>A point-in-time view of a virtual account's payment progress (no PII).</summary>
public sealed record CheckoutSnapshot(
    string AccountRef,
    string AccountNumber,
    string BankName,
    string AccountName,
    string PaymentState,
    long AmountPaidKobo,
    long? ExpectedAmountKobo);

/// <summary>
/// Live Checkout: mint an opaque, expiring token scoped to one virtual account so a payer can
/// poll or stream its reconciliation status. Strictly read-only against the money path —
/// creating or reading a session never credits, settles, or mutates a balance.
/// </summary>
public sealed class CheckoutService(IApplicationDbContext db, ITenantContext tenantContext, IClock clock)
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromDays(1);

    /// <summary>Create a checkout session for one of the current tenant's virtual accounts.</summary>
    public async Task<(CheckoutSession Session, VirtualAccount Account)> CreateSessionAsync(
        string accountRef, TimeSpan? ttl, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var account = await db.VirtualAccounts.FirstOrDefaultAsync(v => v.Reference == accountRef, ct)
            ?? throw new NotFoundException($"Virtual account '{accountRef}' not found.");

        var window = ttl is { } t && t > TimeSpan.Zero ? (t < MaxTtl ? t : MaxTtl) : DefaultTtl;
        var token = "chk_" + Base64Url(RandomNumberGenerator.GetBytes(24));
        var session = new CheckoutSession(tenantId, account.Id, token, clock.UtcNow.Add(window));
        db.CheckoutSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return (session, account);
    }

    /// <summary>Resolve a checkout token to its account (anonymous). Null if unknown or expired.</summary>
    public async Task<(CheckoutSession Session, VirtualAccount Account)?> ResolveAsync(string token, CancellationToken ct = default)
    {
        var session = await db.CheckoutSessions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token, ct);
        if (session is null || session.IsExpired(clock.UtcNow))
            return null;
        var account = await db.VirtualAccounts.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == session.VirtualAccountId, ct);
        return account is null ? null : (session, account);
    }

    public static CheckoutSnapshot Snapshot(VirtualAccount a) => new(
        a.Reference, a.AccountNumber, a.BankName, a.AccountName,
        a.PaymentState.ToString(), a.AmountPaidKobo, a.ExpectedAmountKobo);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
