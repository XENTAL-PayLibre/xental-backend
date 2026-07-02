namespace Xental.Infrastructure.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public URL of the frontend (e.g. https://xental.online). Used for
    /// user-facing pages: email-verified, reset-password, OAuth callback.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Public URL of this API (e.g. https://api.xental.online). Used for the
    /// email-verification magic link, which hits the API then redirects to the frontend.
    /// Falls back to <see cref="BaseUrl"/> when unset.</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    public string EffectiveApiBaseUrl => string.IsNullOrWhiteSpace(ApiBaseUrl) ? BaseUrl : ApiBaseUrl;
}

public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}

public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Email that receives server-error (5xx) alerts. Empty disables alerting.</summary>
    public string Email { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    /// <summary>Suppress repeat alerts for the same error signature within this window.</summary>
    public int ThrottleMinutes { get; set; } = 10;

    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Email);
}
