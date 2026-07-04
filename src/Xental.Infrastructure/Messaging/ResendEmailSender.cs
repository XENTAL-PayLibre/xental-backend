using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Configuration;

namespace Xental.Infrastructure.Messaging;

/// <summary>
/// Transactional email via Resend (https://resend.com). Failures are logged but never
/// thrown: email is best-effort, and surfacing errors on password-reset would leak
/// whether an account exists. Users can always request another link.
/// </summary>
public sealed class ResendEmailSender(
    IHttpClientFactory httpFactory,
    IOptions<ResendOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    private readonly ResendOptions _options = options.Value;

    public Task SendEmailVerificationAsync(string toEmail, string verifyLink, CancellationToken ct = default) =>
        SendAsync(toEmail, "Verify your Xental email",
            $"""
             <p>Welcome to Xental 👋</p>
             <p>Confirm your email address to finish setting up your account:</p>
             <p><a href="{verifyLink}">Verify my email</a></p>
             <p>This link expires soon. If you didn't create a Xental account, you can ignore this email.</p>
             """, ct);

    public Task SendLoginOtpAsync(string toEmail, string code, CancellationToken ct = default) =>
        SendAsync(toEmail, "Your Xental login code",
            $"""
             <p>Your Xental login code is:</p>
             <p style="font-size:28px;font-weight:700;letter-spacing:4px">{System.Net.WebUtility.HtmlEncode(code)}</p>
             <p>It expires in 10 minutes. If you didn't try to sign in, change your password — someone may know it.</p>
             """, ct);

    public Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken ct = default) =>
        SendAsync(toEmail, "Reset your Xental password",
            $"""
             <p>We received a request to reset your Xental password.</p>
             <p><a href="{resetLink}">Choose a new password</a></p>
             <p>This link expires soon. If you didn't request this, you can safely ignore this email — your password won't change.</p>
             """, ct);

    public Task SendTeamInviteAsync(string toEmail, string inviteLink, string accountName, CancellationToken ct = default) =>
        SendAsync(toEmail, $"You've been invited to {accountName} on Xental",
            $"""
             <p>You've been invited to join <strong>{accountName}</strong> on Xental.</p>
             <p>Accept the invitation and set your password to get started:</p>
             <p><a href="{inviteLink}">Accept invitation</a></p>
             <p>This link expires in 7 days. If you weren't expecting this, you can ignore this email.</p>
             """, ct);

    public Task SendOperationalAlertAsync(string toEmail, string subject, string html, CancellationToken ct = default) =>
        SendAsync(toEmail, subject, html, ct);

    public Task SendBillingReminderAsync(
        string toEmail, string brand, long amountKobo, DateTimeOffset dueDateUtc,
        string accountNumber, string bankName, bool overdue, CancellationToken ct = default)
    {
        var amount = "₦" + (amountKobo / 100m).ToString("N2");
        var due = dueDateUtc.ToString("dd MMM yyyy");
        // Defense in depth: the brand is merchant-controlled and rendered inside HTML — always encode it.
        var safeBrand = System.Net.WebUtility.HtmlEncode(brand);
        var subject = overdue
            ? $"Payment overdue — {brand}"
            : $"Payment due — {brand}";
        var lead = overdue
            ? $"<p>Your payment to <strong>{safeBrand}</strong> is now overdue.</p>"
            : $"<p>You have a payment due to <strong>{safeBrand}</strong>.</p>";
        return SendAsync(toEmail, subject,
            $"""
             {lead}
             <p>Amount: <strong>{amount}</strong><br/>Due by: <strong>{due}</strong></p>
             <p>Pay by transferring to your dedicated account:</p>
             <p>Account number: <strong>{accountNumber}</strong><br/>Bank: <strong>{bankName}</strong></p>
             <p>Your payment is applied automatically once it arrives.</p>
             """, ct);
    }

    private async Task SendAsync(string toEmail, string subject, string html, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning("Resend is not configured; skipping email '{Subject}' to {To}.", subject, toEmail);
            return;
        }

        try
        {
            var client = httpFactory.CreateClient("resend");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Content = JsonContent.Create(new
            {
                from = _options.FromEmail,
                to = new[] { toEmail },
                subject,
                html,
            });

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Resend send failed ({Status}) for '{Subject}' to {To}: {Body}",
                    (int)response.StatusCode, subject, toEmail, body);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resend send threw for '{Subject}' to {To}.", subject, toEmail);
        }
    }
}
