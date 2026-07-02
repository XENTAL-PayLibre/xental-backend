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
