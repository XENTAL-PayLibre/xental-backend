using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Nomba;

/// <summary>
/// Southbound Nomba client. Creates a virtual account (NUBAN) under the operator's platform
/// sub-account, authenticated with the cached bearer token + the parent `accountId` header.
///
/// NOTE: the exact Nomba virtual-account request/response contract must be confirmed against
/// Nomba's API reference; the request body + response mapping below are defensive and should
/// be adjusted once verified against the sandbox.
/// </summary>
public sealed class NombaClient(
    IHttpClientFactory httpClientFactory,
    INombaTokenProvider tokenProvider,
    IOptions<NombaOptions> options) : INombaClient
{
    private readonly NombaOptions _options = options.Value;

    public async Task<ProvisionedVirtualAccount> CreateVirtualAccountAsync(
        string accountRef, string accountName, string? email, string? phone, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.SubAccountId))
            throw new NombaIntegrationException("Nomba SubAccountId is not configured.");

        var http = httpClientFactory.CreateClient("nomba");
        var token = await tokenProvider.GetAccessTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"accounts/virtual/{_options.SubAccountId}")
        {
            Content = JsonContent.Create(new
            {
                accountRef,
                accountName,
                email,
                phoneNumber = phone,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(_options.AccountId))
            request.Headers.TryAddWithoutValidation("accountId", _options.AccountId);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new NombaIntegrationException($"Nomba virtual-account creation failed ({(int)response.StatusCode}): {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;

            var accountNumber = Str(data, "bankAccountNumber") ?? Str(data, "accountNumber") ?? Str(data, "accountNo");
            var bankName = Str(data, "bankName") ?? "Nomba";
            var name = Str(data, "bankAccountName") ?? Str(data, "accountName") ?? accountName;
            var providerId = Str(data, "id") ?? Str(data, "accountId") ?? Str(data, "orderReference");

            if (string.IsNullOrWhiteSpace(accountNumber))
                throw new NombaIntegrationException($"Nomba response missing account number: {body}");

            return new ProvisionedVirtualAccount(accountNumber!, bankName, name!, providerId);
        }
        catch (JsonException)
        {
            throw new NombaIntegrationException($"Could not parse Nomba virtual-account response: {body}");
        }
    }

    public async Task<BankAccountName> LookupBankAccountAsync(string accountNumber, string bankCode, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, "transfers/bank/lookup",
            new { accountNumber, bankCode }, ct);
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
        var name = Str(data, "accountName") ?? Str(data, "bankAccountName") ?? Str(data, "name")
            ?? throw new NombaIntegrationException("Nomba lookup did not return an account name.");
        return new BankAccountName(name, accountNumber, bankCode);
    }

    public async Task<TransferResult> InitiateTransferAsync(
        string merchantTxRef, long amountKobo, string accountNumber, string bankCode, string? narration, CancellationToken ct = default)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, "transfers/bank", new
            {
                merchantTxRef,
                amount = amountKobo / 100m, // Nomba expects naira
                accountNumber,
                bankCode,
                narration = narration ?? "Xental settlement",
                senderName = "Xental",
            }, ct);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            var reference = Str(data, "id") ?? Str(data, "transactionId") ?? Str(data, "reference") ?? merchantTxRef;
            var status = Str(data, "status") ?? "PENDING";
            var success = status.Contains("success", StringComparison.OrdinalIgnoreCase)
                          || status is "00" || status.Contains("pending", StringComparison.OrdinalIgnoreCase);
            return new TransferResult(success, reference, success ? null : status);
        }
        catch (NombaIntegrationException ex)
        {
            return new TransferResult(false, null, ex.Message);
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("nomba");
        var token = await tokenProvider.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(_options.AccountId))
            request.Headers.TryAddWithoutValidation("accountId", _options.AccountId);

        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new NombaIntegrationException($"Nomba {path} failed ({(int)response.StatusCode}): {raw}");
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { throw new NombaIntegrationException($"Could not parse Nomba {path} response: {raw}"); }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
