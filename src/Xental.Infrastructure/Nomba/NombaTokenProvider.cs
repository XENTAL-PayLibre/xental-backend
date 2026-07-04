using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Nomba;

/// <summary>Serves a cached Nomba access token, refreshing it in the background window.</summary>
public interface INombaTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Registered as a singleton so the token is cached across requests; refreshes at
/// the configured interval (default 55 min) instead of authenticating on every call.
/// Thread-safe via a single-slot semaphore (no refresh stampede).
///
/// NOTE: the exact Nomba token request/response contract must be confirmed against
/// Nomba's API reference — adjust <see cref="TokenResponse"/> and the request body if needed.
/// </summary>
public sealed class NombaTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<NombaOptions> options,
    IClock clock,
    ILogger<NombaTokenProvider> logger) : INombaTokenProvider
{
    private readonly NombaOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAfter;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null && clock.UtcNow < _refreshAfter)
            return _token;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && clock.UtcNow < _refreshAfter)
                return _token;

            var http = httpClientFactory.CreateClient("nomba");

            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/token/issue")
            {
                Content = JsonContent.Create(new
                {
                    grant_type = "client_credentials",
                    client_id = _options.ClientId,
                    client_secret = _options.ClientSecret,
                }),
            };
            if (!string.IsNullOrWhiteSpace(_options.AccountId))
                request.Headers.TryAddWithoutValidation("accountId", _options.AccountId);

            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            var accessToken = payload?.Data?.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Nomba token response did not contain an access token.");

            _token = accessToken;
            _refreshAfter = clock.UtcNow.AddSeconds(_options.TokenRefreshSeconds);
            logger.LogInformation("Refreshed Nomba access token; next refresh after {RefreshAfter:o}.", _refreshAfter);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("data")] TokenData? Data);

    private sealed record TokenData([property: JsonPropertyName("access_token")] string? AccessToken);
}
