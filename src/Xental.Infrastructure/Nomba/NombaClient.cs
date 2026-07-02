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

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
