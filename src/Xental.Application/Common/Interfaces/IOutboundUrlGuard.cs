namespace Xental.Application.Common.Interfaces;

/// <summary>
/// SSRF protection for developer-supplied webhook URLs: requires HTTPS and rejects hosts that
/// resolve to private, loopback, link-local, or otherwise non-public addresses. Checked at
/// registration and again immediately before each delivery (guards against DNS rebinding).
/// </summary>
public interface IOutboundUrlGuard
{
    /// <summary>Throws ValidationException if the URL is not a safe, public HTTPS endpoint.</summary>
    Task EnsureSafeAsync(string url, CancellationToken ct = default);

    /// <summary>Non-throwing check used on the delivery hot path.</summary>
    Task<bool> IsSafeAsync(string url, CancellationToken ct = default);
}
