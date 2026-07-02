namespace Xental.Infrastructure.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public base URL used to build magic-link + OAuth callback URLs.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";
}

public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}
