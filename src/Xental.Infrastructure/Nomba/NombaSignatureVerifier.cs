using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Nomba;

/// <summary>
/// Verifies Nomba webhook signatures exactly as Nomba computes them:
/// <c>Base64(HMAC-SHA256(secret, payload))</c> where payload is a colon-delimited string of
/// nine fields in order — event_type, requestId, merchant.userId, merchant.walletId,
/// transaction.transactionId, transaction.type, transaction.time, transaction.responseCode
/// ("" if null), and the <c>nomba-timestamp</c> header. Compared against the
/// <c>nomba-signature</c> header (constant-time). Fails closed if no secret is configured.
/// See https://developer.nomba.com/docs/api-basics/webhook.
/// </summary>
public sealed class NombaSignatureVerifier(IOptions<NombaOptions> options, IClock clock) : INombaSignatureVerifier
{
    private readonly NombaOptions _options = options.Value;

    public bool Verify(byte[] rawBody, string? signatureHeader, string? timestampHeader)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        // Bound the replay window: a captured-but-valid envelope can't be replayed indefinitely.
        // Deliberately generous (default 24h) and fail-open when the timestamp can't be parsed, so
        // legitimate — sometimes long-delayed — Nomba retries are never dropped; the HMAC still gates
        // authenticity and the transaction-reference dedupe still blocks double-credit.
        if (_options.WebhookMaxAgeMinutes > 0 && TryParseTimestamp(timestampHeader, out var sentAt))
        {
            var age = clock.UtcNow - sentAt;
            if (age > TimeSpan.FromMinutes(_options.WebhookMaxAgeMinutes) || age < TimeSpan.FromMinutes(-_options.WebhookMaxAgeMinutes))
                return false;
        }

        string hashingPayload;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : default;
            var merchant = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("merchant", out var m) ? m : default;
            var txn = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("transaction", out var t) ? t : default;

            hashingPayload = string.Join(":",
                Str(root, "event_type"),
                Str(root, "requestId"),
                Str(merchant, "userId"),
                Str(merchant, "walletId"),
                Str(txn, "transactionId"),
                Str(txn, "type"),
                Str(txn, "time"),
                Str(txn, "responseCode"),
                timestampHeader ?? string.Empty);
        }
        catch (JsonException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(hashingPayload)));

        // Compare the exact Base64 bytes (case-sensitive — Base64 is). Constant-time.
        var a = Encoding.UTF8.GetBytes(computed);
        var b = Encoding.UTF8.GetBytes(signatureHeader.Trim());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Parse the <c>nomba-timestamp</c> header, accepting epoch seconds, epoch milliseconds, or
    /// an ISO-8601 datetime. Returns false (skip the freshness check) for anything else.</summary>
    private static bool TryParseTimestamp(string? header, out DateTimeOffset when)
    {
        when = default;
        if (string.IsNullOrWhiteSpace(header)) return false;
        var s = header.Trim();
        if (long.TryParse(s, out var epoch))
        {
            // Heuristic: 13+ digits => milliseconds, otherwise seconds.
            when = s.Length >= 13 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out when);
    }

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
