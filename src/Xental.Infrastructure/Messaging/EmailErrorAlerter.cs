using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Configuration;

namespace Xental.Infrastructure.Messaging;

/// <summary>
/// Emails operators when an unhandled 5xx occurs. Throttles by (exception type + message +
/// path) so a burst of the same fault produces one alert per window. Best-effort — a failed
/// alert never affects the request (the underlying sender swallows its own errors). Registered
/// as a singleton (so the throttle cache persists); resolves the scoped sender per alert.
/// </summary>
public sealed class EmailErrorAlerter(
    IServiceScopeFactory scopeFactory,
    IOptions<AlertOptions> options,
    IClock clock) : IErrorAlerter
{
    private readonly AlertOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new();

    public async Task NotifyServerErrorAsync(Exception exception, string path, string method, string? traceId, CancellationToken ct = default)
    {
        if (!_options.IsActive)
            return;

        var msg = exception.Message.Length > 80 ? exception.Message[..80] : exception.Message;
        var signature = $"{exception.GetType().FullName}|{msg}|{path}";
        var now = clock.UtcNow;

        if (_recent.TryGetValue(signature, out var last) && now - last < TimeSpan.FromMinutes(_options.ThrottleMinutes))
            return;
        _recent[signature] = now;
        Prune(now);

        var subject = $"[Xental] Server error: {exception.GetType().Name} on {method} {path}";
        var html =
            $"""
             <p><b>{WebUtility.HtmlEncode(method)} {WebUtility.HtmlEncode(path)}</b> returned 500.</p>
             <p>Time: {now:o}<br/>Trace: {WebUtility.HtmlEncode(traceId ?? "-")}</p>
             <pre style="white-space:pre-wrap">{WebUtility.HtmlEncode(exception.ToString())}</pre>
             """;

        using var scope = scopeFactory.CreateScope();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        await email.SendOperationalAlertAsync(_options.Email, subject, html, ct);
    }

    private void Prune(DateTimeOffset now)
    {
        if (_recent.Count < 500) return;
        foreach (var kv in _recent)
            if (now - kv.Value > TimeSpan.FromHours(1))
                _recent.TryRemove(kv.Key, out _);
    }
}
