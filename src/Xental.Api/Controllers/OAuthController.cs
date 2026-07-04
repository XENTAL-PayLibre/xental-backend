using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Xental.Api.Auth;
using Xental.Application.Authentication;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Configuration;

namespace Xental.Api.Controllers;

/// <summary>
/// Social login (the dashboard plane). Start a login to be redirected to the provider;
/// the provider redirects back to the callback, which sets the session cookies and
/// forwards the browser to the app. Supported providers: <c>google</c>, <c>github</c>.
/// </summary>
[ApiController]
[Route("api/v1/auth/oauth")]
[AllowAnonymous]
[EnableRateLimiting("auth")]
public sealed class OAuthController(
    OAuthLoginService oauth,
    AuthCookieWriter cookies,
    ITokenGenerator tokens,
    IOptions<AppOptions> app) : ControllerBase
{
    private const string StateCookie = "xnt_oauth_state";

    /// <summary>Begin social login: redirects to the provider's consent screen.</summary>
    /// <response code="302">Redirects to the provider.</response>
    [HttpGet("{provider}")]
    public IActionResult Start(string provider)
    {
        var redirectUri = CallbackUri(provider);
        var state = tokens.Generate("st", 16);

        Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax, // sent on the top-level GET redirect back
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/",
        });

        return Redirect(oauth.AuthorizationUrl(provider, redirectUri, state));
    }

    /// <summary>Provider callback: exchanges the code and forwards to the app with a token.</summary>
    /// <response code="302">Sets session cookies and redirects to the app with <c>#status=ok</c> or <c>#error=…</c>.</response>
    [HttpGet("{provider}/callback")]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        CancellationToken ct)
    {
        var appBase = app.Value.BaseUrl.TrimEnd('/');
        var expectedState = Request.Cookies[StateCookie];
        Response.Cookies.Delete(StateCookie);

        // CSRF: the state we set must round-trip back unchanged.
        if (string.IsNullOrEmpty(state) || state != expectedState)
            return Redirect($"{appBase}/auth/callback#error=invalid_state");

        try
        {
            var session = await oauth.CompleteAsync(provider, code ?? string.Empty, CallbackUri(provider), ct);
            cookies.SetSession(Response, session); // session travels in HttpOnly cookies, not the URL
            return Redirect($"{appBase}/auth/callback#status=ok");
        }
        catch (Exception)
        {
            return Redirect($"{appBase}/auth/callback#error=login_failed");
        }
    }

    private string CallbackUri(string provider) =>
        $"{Request.Scheme}://{Request.Host}/api/v1/auth/oauth/{provider.ToLowerInvariant()}/callback";
}
