namespace Xental.Application.Common.Interfaces;

/// <summary>A reconciliation status update for one virtual account, pushed to Live Checkout subscribers.</summary>
public sealed record CheckoutStatusEvent(
    Guid VirtualAccountId,
    string AccountRef,
    string PaymentState,
    long AmountPaidKobo,
    long? ExpectedAmountKobo,
    string Reconciliation);

/// <summary>
/// In-process pub/sub bridging the reconciliation engine to Live Checkout SSE streams. A pure
/// notify path: publishing is best-effort, non-blocking, and can never affect money movement —
/// a failure here is swallowed so it cannot impact reconciliation.
/// </summary>
public interface IReconciliationNotifier
{
    /// <summary>Publish a status update to any open subscribers for the account (non-blocking, best-effort).</summary>
    void Publish(CheckoutStatusEvent evt);

    /// <summary>Stream status updates for one virtual account until the token is cancelled.</summary>
    IAsyncEnumerable<CheckoutStatusEvent> SubscribeAsync(Guid virtualAccountId, CancellationToken ct);
}
