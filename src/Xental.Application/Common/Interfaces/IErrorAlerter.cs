namespace Xental.Application.Common.Interfaces;

/// <summary>
/// Notifies operators of unhandled server errors (5xx). Implementations throttle by error
/// signature so a burst of the same fault sends one alert, not thousands.
/// </summary>
public interface IErrorAlerter
{
    Task NotifyServerErrorAsync(Exception exception, string path, string method, string? traceId, CancellationToken ct = default);

    /// <summary>Send an operational alert (e.g. a failed/held settlement). Throttled by
    /// <paramref name="dedupeKey"/> so repeats within the window collapse to one email.</summary>
    Task NotifyOperationalAsync(string subject, string message, string dedupeKey, CancellationToken ct = default);
}
