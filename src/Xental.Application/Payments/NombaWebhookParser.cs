using System.Globalization;
using System.Text.Json;

namespace Xental.Application.Payments;

/// <summary>A transfer parsed from a Nomba webhook (gross amount, fee, payer name, etc.).</summary>
public sealed record NombaInflow(
    string Reference,
    string AccountNumber,
    long AmountKobo,
    long FeeKobo,
    string? TransferName,
    string EventType,
    DateTimeOffset OccurredAtUtc)
{
    public bool IsReversal => EventType.Contains("revers", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Best-effort parser for Nomba's virtual-account payment webhook. Extracts the fields the
/// reconciliation engine needs. NOTE: field candidates are matched defensively; confirm/adjust
/// against a captured live payload (see https://developer.nomba.com/docs/api-basics/webhook).
/// </summary>
public static class NombaWebhookParser
{
    public static bool TryParse(byte[] rawBody, out NombaInflow inflow)
    {
        inflow = null!;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var eventType = Str(root, "event_type") ?? Str(root, "eventType") ?? string.Empty;
            var data = root.TryGetProperty("data", out var d) ? d : root;
            var txn = data.TryGetProperty("transaction", out var t) ? t : data;
            var customer = data.TryGetProperty("customer", out var c) ? c : default;

            var reference = Str(txn, "transactionId") ?? Str(txn, "transaction_id")
                            ?? Str(root, "requestId") ?? Str(root, "request_id");
            var accountNumber = Str(txn, "aliasAccountNumber") ?? Str(txn, "alias_account_number")
                                ?? Str(txn, "accountNumber") ?? Str(customer, "accountNumber");
            var amountKobo = Kobo(txn, "transactionAmount") ?? Kobo(txn, "amount")
                             ?? Kobo(data.TryGetProperty("order", out var o) ? o : txn, "amount");
            var feeKobo = Kobo(txn, "fee") ?? Kobo(txn, "transactionFee") ?? 0;
            var transferName = Str(txn, "senderName") ?? Str(txn, "originatorName") ?? Str(txn, "payerName")
                               ?? Str(customer, "senderName") ?? Str(customer, "name");
            var occurred = Time(txn, "time") ?? Time(txn, "transactionTime");

            if (string.IsNullOrWhiteSpace(reference) || amountKobo is not > 0)
                return false;

            inflow = new NombaInflow(reference!, accountNumber ?? string.Empty, amountKobo.Value, feeKobo,
                transferName, eventType, occurred ?? DateTimeOffset.UnixEpoch);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A successful deposit (as opposed to a reversal / other event).</summary>
    public static bool IsSuccess(byte[] rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var eventType = Str(root, "event_type") ?? Str(root, "eventType");
            var data = root.TryGetProperty("data", out var d) ? d : root;
            var txn = data.TryGetProperty("transaction", out var t) ? t : data;
            var rc = Str(txn, "responseCode") ?? Str(txn, "response_code");
            return string.Equals(eventType, "payment_success", StringComparison.OrdinalIgnoreCase) || rc == "00";
        }
        catch { return false; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null)
            : null;

    private static long? Kobo(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var a)) return null;
        decimal naira;
        if (a.ValueKind == JsonValueKind.Number && a.TryGetDecimal(out naira))
            return (long)decimal.Round(naira * 100m, 0, MidpointRounding.ToEven);
        if (a.ValueKind == JsonValueKind.String &&
            decimal.TryParse(a.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out naira))
            return (long)decimal.Round(naira * 100m, 0, MidpointRounding.ToEven);
        return null;
    }

    private static DateTimeOffset? Time(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : null;
}
