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

    public Task SendOnboardingReviewAlertAsync(string toEmail, string track, string applicantName, CancellationToken ct = default)
    {
        var safeTrack = System.Net.WebUtility.HtmlEncode(track);
        var safeName = System.Net.WebUtility.HtmlEncode(applicantName);
        return SendAsync(toEmail, $"New {track} submitted for review",
            $"""
             <p>A new <strong>{safeTrack}</strong> submission is awaiting review.</p>
             <p>Applicant: <strong>{safeName}</strong></p>
             <p>Sign in to the Xental admin console to review the details and approve or reject it.</p>
             """, ct);
    }

    public Task SendCustomerAccountDetailsAsync(
        string toEmail, string businessName, string accountNumber, string bankName,
        string accountName, long? expectedAmountKobo, CancellationToken ct = default)
    {
        var safeBusiness = System.Net.WebUtility.HtmlEncode(businessName);
        var amountLine = expectedAmountKobo is long k
            ? $"<p>Amount to pay: <strong>₦{(k / 100m).ToString("N2")}</strong></p>"
            : "";
        // The sender display name is the merchant's business name (fromName); the address stays
        // the verified Xental sender.
        return SendAsync(toEmail, $"Your payment account with {businessName}",
            $"""
             <p><strong>{safeBusiness}</strong> has set up a dedicated account for your payments.</p>
             <p>Pay by transferring to:</p>
             <p>Account number: <strong>{System.Net.WebUtility.HtmlEncode(accountNumber)}</strong><br/>
             Bank: <strong>{System.Net.WebUtility.HtmlEncode(bankName)}</strong><br/>
             Account name: <strong>{System.Net.WebUtility.HtmlEncode(accountName)}</strong></p>
             {amountLine}
             <p>Your payment is applied automatically once it arrives.</p>
             """, ct, fromName: businessName);
    }

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

    /// <summary>Build the RFC5322 From value. An optional display name (e.g. the merchant's business
    /// name) is prepended while the address stays the verified sender: <c>Name &lt;addr&gt;</c>.
    /// The name is sanitised to prevent header injection.</summary>
    private string From(string? fromName)
    {
        if (string.IsNullOrWhiteSpace(fromName)) return _options.FromEmail;
        var clean = new string(fromName.Where(c => c is not ('"' or '<' or '>' or '\r' or '\n')).ToArray()).Trim();
        return clean.Length == 0 ? _options.FromEmail : $"{clean} <{_options.FromEmail}>";
    }

    private async Task SendAsync(string toEmail, string subject, string html, CancellationToken ct, string? fromName = null)
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
                from = From(fromName),
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
