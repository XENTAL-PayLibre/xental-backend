namespace Xental.Api.Middleware;

/// <summary>
/// Adds baseline hardening response headers to every response. The API is JSON-only (no HTML
/// rendering), so a locked-down CSP + nosniff + framing/referrer controls carry no UI cost.
/// HSTS is emitted only over HTTPS so local HTTP dev is unaffected.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // Swagger UI serves HTML/JS/CSS; the strict JSON CSP would break it. Leave the docs alone.
        if (context.Request.Path.StartsWithSegments("/swagger"))
            return next(context);

        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        if (context.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        return next(context);
    }
}
