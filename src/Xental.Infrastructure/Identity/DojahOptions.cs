namespace Xental.Infrastructure.Identity;

/// <summary>
/// Dojah identity-verification config. The free sandbox and live share the same endpoints — only
/// the base URL + keys differ. Credentials are secrets (env); BaseUrl is non-secret.
/// </summary>
public sealed class DojahOptions
{
    public const string SectionName = "Dojah";

    /// <summary>Sandbox: https://sandbox.dojah.io — Live: https://api.dojah.io</summary>
    public string BaseUrl { get; set; } = "https://sandbox.dojah.io";
    public string AppId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(SecretKey);
}
