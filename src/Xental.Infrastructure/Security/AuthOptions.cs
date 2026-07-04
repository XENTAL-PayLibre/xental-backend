namespace Xental.Infrastructure.Security;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Bcrypt work factor for password hashing (>= 12).</summary>
    public int BcryptWorkFactor { get; set; } = 12;

    /// <summary>Minutes a magic-link email-verification token stays valid.</summary>
    public int EmailVerificationTtlMinutes { get; set; } = 30;

    /// <summary>Minutes a password-reset link stays valid.</summary>
    public int PasswordResetTtlMinutes { get; set; } = 30;

    /// <summary>Days a dashboard refresh token stays valid.</summary>
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Cookie domain for the auth cookies (e.g. ".xental.online"). Empty = host-only.</summary>
    public string CookieDomain { get; set; } = string.Empty;

    /// <summary>
    /// SameSite policy for the auth cookies: "Lax" (default), "None", or "Strict".
    /// Use "Lax" when the frontend and API share a registrable domain (e.g. app + api
    /// under xental.online). Use "None" when the browser talks to the API from a
    /// different site — e.g. a local dev frontend on http://localhost:3000 hitting the
    /// deployed staging API — otherwise the browser never attaches the cookie on those
    /// cross-site calls. "None" is only honoured over HTTPS (it forces the Secure flag).
    /// </summary>
    public string CookieSameSite { get; set; } = "Lax";

    /// <summary>
    /// Comma-separated request Origins (e.g. "http://localhost:3000") that receive a
    /// development-friendly session cookie: <b>no Domain</b> (host-only), <b>Secure=false</b>, and
    /// <b>SameSite=Lax</b>. This is what lets a plain-http local dev server / Next.js proxy store
    /// and read the HttpOnly cookie it relays. Matched exactly against the incoming Origin header,
    /// so production origins are unaffected. Never list a production origin. Empty disables it.
    /// </summary>
    public string DevInsecureCookieOrigins { get; set; } = string.Empty;

    /// <summary>Whether auth cookies require HTTPS (Secure flag). True in real environments.</summary>
    public bool CookieSecure { get; set; } = true;

    public OAuthProviderOptions Google { get; set; } = new();
    public OAuthProviderOptions GitHub { get; set; } = new();
}

public sealed class OAuthProviderOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
