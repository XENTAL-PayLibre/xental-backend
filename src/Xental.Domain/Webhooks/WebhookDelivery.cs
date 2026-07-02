using Xental.Domain.Common;

namespace Xental.Domain.Webhooks;

public enum WebhookDeliveryStatus { Pending = 1, Delivered = 2, Failed = 3, DeadLetter = 4 }

/// <summary>
/// A single at-least-once delivery attempt record for an outbound event. Retried with
/// exponential backoff up to a cap, then dead-lettered (and replayable). The event id makes
/// the delivered payload idempotent for the receiver.
/// </summary>
public sealed class WebhookDelivery : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid EndpointId { get; private set; }
    public string EventId { get; private set; } = null!;
    public string EventType { get; private set; } = null!;
    public string PayloadJson { get; private set; } = null!;
    public WebhookDeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? DeliveredAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public int? LastStatusCode { get; private set; }

    private WebhookDelivery() { } // EF

    public WebhookDelivery(Guid tenantId, Guid endpointId, string eventId, string eventType, string payloadJson, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (endpointId == Guid.Empty) throw new DomainException("EndpointId is required.");
        TenantId = tenantId;
        EndpointId = endpointId;
        EventId = DomainException.Require(eventId, nameof(eventId));
        EventType = DomainException.Require(eventType, nameof(eventType));
        PayloadJson = DomainException.Require(payloadJson, nameof(payloadJson));
        Status = WebhookDeliveryStatus.Pending;
        NextAttemptAtUtc = now;
    }

    public bool IsDue(DateTimeOffset now) =>
        (Status == WebhookDeliveryStatus.Pending || Status == WebhookDeliveryStatus.Failed)
        && NextAttemptAtUtc is { } next && now >= next;

    public void MarkDelivered(DateTimeOffset at, int statusCode)
    {
        Attempts++;
        Status = WebhookDeliveryStatus.Delivered;
        DeliveredAtUtc = at;
        LastStatusCode = statusCode;
        NextAttemptAtUtc = null;
        LastError = null;
    }

    /// <summary>Record a failed attempt: reschedule with backoff, or dead-letter at the cap.</summary>
    public void RecordFailure(string error, int? statusCode, DateTimeOffset now, int maxAttempts)
    {
        Attempts++;
        LastError = error.Length > 500 ? error[..500] : error;
        LastStatusCode = statusCode;
        if (Attempts >= maxAttempts)
        {
            Status = WebhookDeliveryStatus.DeadLetter;
            NextAttemptAtUtc = null;
        }
        else
        {
            Status = WebhookDeliveryStatus.Failed;
            // Exponential backoff: 1m, 2m, 4m, 8m … capped at 1h.
            var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Attempts - 1)));
            NextAttemptAtUtc = now + delay;
        }
    }

    /// <summary>Manually requeue a dead-lettered/failed delivery for immediate retry.</summary>
    public void Replay(DateTimeOffset now)
    {
        Status = WebhookDeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = now;
        LastError = null;
    }
}
