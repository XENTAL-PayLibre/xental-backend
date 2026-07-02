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
public sealed class NombaSignatureVerifier(IOptions<NombaOptions> options) : INombaSignatureVerifier
{
    private readonly NombaOptions _options = options.Value;

    public bool Verify(byte[] rawBody, string? signatureHeader, string? timestampHeader)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

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

        var provided = signatureHeader.Trim();
        var a = Encoding.UTF8.GetBytes(computed.ToLowerInvariant());
        var b = Encoding.UTF8.GetBytes(provided.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
