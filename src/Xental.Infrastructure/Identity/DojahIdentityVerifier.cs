using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Identity;

/// <summary>
/// Dojah adapter for BVN/NIN/CAC lookups (data checks only — no biometrics). Defensive parsing
/// (the provider's exact JSON can vary): a missing/blocked field yields "not found", never an
/// exception. The raw id number is sent only over TLS and never logged.
/// </summary>
public sealed class DojahIdentityVerifier(
    IHttpClientFactory httpFactory,
    IOptions<DojahOptions> options,
    ILogger<DojahIdentityVerifier> logger) : IIdentityVerifier
{
    private readonly DojahOptions _options = options.Value;

    public Task<IdentityResult> VerifyBvnAsync(string bvn, CancellationToken ct = default) =>
        LookupIdentityAsync($"api/v1/kyc/bvn/full?bvn={Uri.EscapeDataString(bvn.Trim())}", "bvn", ct);

    public Task<IdentityResult> VerifyNinAsync(string nin, CancellationToken ct = default) =>
        LookupIdentityAsync($"api/v1/kyc/nin?nin={Uri.EscapeDataString(nin.Trim())}", "nin", ct);

    public async Task<CompanyResult> VerifyCacAsync(string rcNumber, CancellationToken ct = default)
    {
        var entity = await GetEntityAsync($"api/v1/kyc/cac?rc_number={Uri.EscapeDataString(rcNumber.Trim())}", "cac", ct);
        if (entity is not { ValueKind: JsonValueKind.Object }) return CompanyResult.NotFound();
        var name = Str(entity.Value, "company_name") ?? Str(entity.Value, "companyName") ?? Str(entity.Value, "name");
        return string.IsNullOrWhiteSpace(name)
            ? CompanyResult.NotFound()
            : new CompanyResult(true, name, Str(entity.Value, "rc_number") ?? rcNumber);
    }

    private async Task<IdentityResult> LookupIdentityAsync(string path, string kind, CancellationToken ct)
    {
        var entity = await GetEntityAsync(path, kind, ct);
        if (entity is not { ValueKind: JsonValueKind.Object }) return IdentityResult.NotFound();
        var e = entity.Value;
        var first = Str(e, "first_name") ?? Str(e, "firstName");
        var last = Str(e, "last_name") ?? Str(e, "lastName");
        if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(last))
            return IdentityResult.NotFound();
        return new IdentityResult(true, first, last, ParseDate(Str(e, "date_of_birth") ?? Str(e, "dob")));
    }

    /// <summary>GET the Dojah endpoint and return its <c>entity</c> object (or null on any failure).</summary>
    private async Task<JsonElement?> GetEntityAsync(string path, string kind, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning("Dojah is not configured; skipping {Kind} check.", kind);
            return null;
        }
        try
        {
            var client = httpFactory.CreateClient("dojah");
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("AppId", _options.AppId);
            request.Headers.Add("Authorization", _options.SecretKey);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Dojah {Kind} lookup returned {Status}.", kind, (int)response.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            // Never log the payload — it contains PII.
            return doc.RootElement.TryGetProperty("entity", out var entity)
                ? entity.Clone()
                : (JsonElement?)null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dojah {Kind} lookup failed.", kind);
            return null;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
