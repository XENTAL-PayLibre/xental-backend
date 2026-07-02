using Microsoft.Extensions.Options;
using Xental.Application.Authentication;
using Xental.Infrastructure.Security;

namespace Xental.Api.Auth;

/// <summary>
/// Writes/clears the dashboard session cookies. The access + refresh tokens are only
/// ever delivered as HttpOnly, Secure, SameSite cookies — never in a response body —
/// so browser JS can't read them and they only travel over HTTPS.
/// </summary>
public sealed class AuthCookieWriter(IOptions<AuthOptions> auth)
{
    public const string AccessCookie = "xnt_access";
    public const string RefreshCookie = "xnt_refresh";

    private readonly AuthOptions _auth = auth.Value;

    public void SetSession(HttpResponse response, IssuedSession session)
    {
        response.Cookies.Append(AccessCookie, session.Access.Token, BuildOptions(session.Access.ExpiresAt));
        response.Cookies.Append(RefreshCookie, session.RefreshToken, BuildOptions(session.RefreshExpiresAtUtc));
    }

    public void Clear(HttpResponse response)
    {
        var opts = BuildOptions(DateTimeOffset.UnixEpoch);
        response.Cookies.Delete(AccessCookie, opts);
        response.Cookies.Delete(RefreshCookie, opts);
    }

    private CookieOptions BuildOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = _auth.CookieSecure,
        SameSite = SameSiteMode.Lax,
        Expires = expires,
        Path = "/",
        Domain = string.IsNullOrWhiteSpace(_auth.CookieDomain) ? null : _auth.CookieDomain,
    };
}
