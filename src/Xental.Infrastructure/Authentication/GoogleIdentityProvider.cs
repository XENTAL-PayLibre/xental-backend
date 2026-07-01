using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Security;

namespace Xental.Infrastructure.Authentication;

/// <summary>Google OAuth2 / OpenID Connect identity provider.</summary>
public sealed class GoogleIdentityProvider(
    IHttpClientFactory httpFactory,
    IOptions<AuthOptions> options) : IExternalIdentityProvider
{
    private const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    private readonly OAuthProviderOptions _options = options.Value.Google;

    public string Name => "google";

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        EnsureConfigured();
        return OAuthQuery.Build(AuthorizeUrl, new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["access_type"] = "online",
            ["prompt"] = "select_account",
        });
    }

    public async Task<ExternalUserProfile> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        EnsureConfigured();
        var client = httpFactory.CreateClient("oauth");

        using var tokenResponse = await client.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }), ct);

        if (!tokenResponse.IsSuccessStatusCode)
            throw new AuthenticationException("Google token exchange failed.");

        using var tokenDoc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();

        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
        infoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var infoResponse = await client.SendAsync(infoRequest, ct);
        if (!infoResponse.IsSuccessStatusCode)
            throw new AuthenticationException("Failed to read Google profile.");

        using var infoDoc = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync(ct));
        var root = infoDoc.RootElement;
        var sub = root.GetProperty("sub").GetString()!;
        var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;

        var emailVerified = root.TryGetProperty("email_verified", out var v) &&
            (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));
        if (string.IsNullOrWhiteSpace(email) || !emailVerified)
            throw new AuthenticationException("Google account has no verified email.");

        return new ExternalUserProfile(Name, sub, email!, name);
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
            throw new AuthenticationException("Google login is not configured.");
    }
}
