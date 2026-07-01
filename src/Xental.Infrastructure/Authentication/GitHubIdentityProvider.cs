using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Security;

namespace Xental.Infrastructure.Authentication;

/// <summary>GitHub OAuth2 identity provider.</summary>
public sealed class GitHubIdentityProvider(
    IHttpClientFactory httpFactory,
    IOptions<AuthOptions> options) : IExternalIdentityProvider
{
    private const string AuthorizeUrl = "https://github.com/login/oauth/authorize";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string UserUrl = "https://api.github.com/user";
    private const string EmailsUrl = "https://api.github.com/user/emails";

    private readonly OAuthProviderOptions _options = options.Value.GitHub;

    public string Name => "github";

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        EnsureConfigured();
        return OAuthQuery.Build(AuthorizeUrl, new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "read:user user:email",
            ["state"] = state,
        });
    }

    public async Task<ExternalUserProfile> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        EnsureConfigured();
        var client = httpFactory.CreateClient("oauth");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            }),
        };
        tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var tokenResponse = await client.SendAsync(tokenRequest, ct);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new AuthenticationException("GitHub token exchange failed.");

        using var tokenDoc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var tokenElement))
            throw new AuthenticationException("GitHub did not return an access token.");
        var accessToken = tokenElement.GetString();

        var root = await GetJsonAsync(client, UserUrl, accessToken!, ct);
        var id = root.GetProperty("id").GetRawText();           // numeric id
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;

        // GitHub often hides the primary email on /user; fall back to /user/emails.
        email ??= await GetPrimaryVerifiedEmailAsync(client, accessToken!, ct);
        if (string.IsNullOrWhiteSpace(email))
            throw new AuthenticationException("GitHub account has no verified email.");

        return new ExternalUserProfile(Name, id, email!, name);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Xental", "1.0")); // GitHub requires a UA

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthenticationException("Failed to read GitHub profile.");

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    private static async Task<string?> GetPrimaryVerifiedEmailAsync(HttpClient client, string accessToken, CancellationToken ct)
    {
        var emails = await GetJsonAsync(client, EmailsUrl, accessToken, ct);
        if (emails.ValueKind != JsonValueKind.Array)
            return null;

        string? firstVerified = null;
        foreach (var item in emails.EnumerateArray())
        {
            var verified = item.TryGetProperty("verified", out var v) && v.GetBoolean();
            if (!verified)
                continue;
            var address = item.GetProperty("email").GetString();
            if (item.TryGetProperty("primary", out var p) && p.GetBoolean())
                return address;
            firstVerified ??= address;
        }
        return firstVerified;
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
            throw new AuthenticationException("GitHub login is not configured.");
    }
}
