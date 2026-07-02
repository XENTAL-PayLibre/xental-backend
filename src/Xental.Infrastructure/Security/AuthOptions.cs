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

    public OAuthProviderOptions Google { get; set; } = new();
    public OAuthProviderOptions GitHub { get; set; } = new();
}

public sealed class OAuthProviderOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
