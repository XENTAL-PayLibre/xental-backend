using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Configuration;
using Xental.Infrastructure.Security;

namespace Xental.Infrastructure.Messaging;

/// <summary>Builds email links from the configured app base URL, and owns token TTLs.</summary>
public sealed class AppLinkBuilder(IOptions<AppOptions> app, IOptions<AuthOptions> auth) : ILinkBuilder
{
    private readonly string _baseUrl = app.Value.BaseUrl.TrimEnd('/');
    private readonly AuthOptions _auth = auth.Value;

    // Clicking this hits the API, which verifies then redirects to the app.
    public string EmailVerificationLink(string rawToken) =>
        $"{_baseUrl}/api/v1/developers/verify-email?token={Uri.EscapeDataString(rawToken)}";

    // Points at the frontend page that collects a new password and POSTs the reset.
    public string PasswordResetLink(string rawToken) =>
        $"{_baseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";

    public TimeSpan EmailVerificationTtl => TimeSpan.FromMinutes(_auth.EmailVerificationTtlMinutes);
    public TimeSpan PasswordResetTtl => TimeSpan.FromMinutes(_auth.PasswordResetTtlMinutes);
}
