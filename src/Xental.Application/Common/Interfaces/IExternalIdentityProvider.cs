namespace Xental.Application.Common.Interfaces;

/// <summary>A verified identity returned by an OAuth provider after code exchange.</summary>
public sealed record ExternalUserProfile(string Provider, string ProviderUserId, string Email, string? Name);

/// <summary>
/// An OAuth2 identity provider (Google, GitHub). Builds the authorization URL the
/// browser is redirected to, and exchanges the returned code for the user's profile.
/// </summary>
public interface IExternalIdentityProvider
{
    /// <summary>Lower-case provider key, e.g. "google" or "github".</summary>
    string Name { get; }

    string BuildAuthorizationUrl(string redirectUri, string state);

    Task<ExternalUserProfile> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);
}
