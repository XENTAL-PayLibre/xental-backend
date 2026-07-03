using Microsoft.Extensions.Options;
using Xental.Application.Authentication;
using Xental.Infrastructure.Security;

namespace Xental.Api.Auth;

/// <summary>
/// Writes/clears the dashboard session cookies. The access + refresh tokens are only
/// ever delivered as HttpOnly, Secure, SameSite cookies — never in a response body —
/// so browser JS can't read them and they only travel over HTTPS.
/// </summary>
public sealed class AuthCookieWriter(IOptions<AuthOptions> auth, IHttpContextAccessor http)
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

    private CookieOptions BuildOptions(DateTimeOffset expires)
    {
        // Local-dev override: for a whitelisted plain-http origin (e.g. a localhost:3000 Next.js
        // dev server that relays the login response), emit a host-only, non-secure, Lax cookie —
        // the only shape a browser will store + return over http on a different host. Keyed to the
        // exact Origin header, so production origins never hit this branch.
        if (IsDevInsecureOrigin())
            return new()
            {
                HttpOnly = true,
                Secure = false,
                SameSite = SameSiteMode.Lax,
                Expires = expires,
                Path = "/",
                Domain = null, // host-only — binds to the host that relays it (the dev proxy)
            };

        var sameSite = ParseSameSite(_auth.CookieSameSite);
        return new()
        {
            HttpOnly = true,
            // SameSite=None is rejected by browsers unless the cookie is also Secure, so
            // force Secure in that case regardless of config.
            Secure = _auth.CookieSecure || sameSite == SameSiteMode.None,
            SameSite = sameSite,
            Expires = expires,
            Path = "/",
            Domain = string.IsNullOrWhiteSpace(_auth.CookieDomain) ? null : _auth.CookieDomain,
        };
    }

    /// <summary>True when the request's Origin is in the configured dev-insecure allow-list.</summary>
    private bool IsDevInsecureOrigin()
    {
        if (string.IsNullOrWhiteSpace(_auth.DevInsecureCookieOrigins))
            return false;
        var origin = http.HttpContext?.Request.Headers["Origin"].ToString();
        if (string.IsNullOrWhiteSpace(origin))
            return false;
        return _auth.DevInsecureCookieOrigins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
    }

    private static SameSiteMode ParseSameSite(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => SameSiteMode.None,
        "strict" => SameSiteMode.Strict,
        _ => SameSiteMode.Lax,
    };
}
