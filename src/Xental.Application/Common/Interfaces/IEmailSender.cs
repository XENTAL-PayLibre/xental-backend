namespace Xental.Application.Common.Interfaces;

/// <summary>Sends transactional email (magic-link verification, password reset, etc.).</summary>
public interface IEmailSender
{
    Task SendEmailVerificationAsync(string toEmail, string verifyLink, CancellationToken ct = default);
    Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken ct = default);
    Task SendTeamInviteAsync(string toEmail, string inviteLink, string accountName, CancellationToken ct = default);
    Task SendOperationalAlertAsync(string toEmail, string subject, string html, CancellationToken ct = default);

    /// <summary>Dunning notice to a customer: a billing period is due (or overdue). Tells them how
    /// much to pay and into which account. <paramref name="brand"/> is the merchant's display name.</summary>
    Task SendBillingReminderAsync(
        string toEmail, string brand, long amountKobo, DateTimeOffset dueDateUtc,
        string accountNumber, string bankName, bool overdue, CancellationToken ct = default);
}
