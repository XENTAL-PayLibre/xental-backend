namespace Xental.Application.Common.Interfaces;

/// <summary>Sends transactional email (magic-link verification, password reset, etc.).</summary>
public interface IEmailSender
{
    Task SendEmailVerificationAsync(string toEmail, string verifyLink, CancellationToken ct = default);

    /// <summary>Second-factor login code (6-digit OTP) emailed after a correct password.</summary>
    Task SendLoginOtpAsync(string toEmail, string code, CancellationToken ct = default);
    Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken ct = default);
    Task SendTeamInviteAsync(string toEmail, string inviteLink, string accountName, CancellationToken ct = default);
    Task SendOperationalAlertAsync(string toEmail, string subject, string html, CancellationToken ct = default);

    /// <summary>Notify an admin that an onboarding track (KYC or KYB) was submitted and awaits review.</summary>
    Task SendOnboardingReviewAlertAsync(string toEmail, string track, string applicantName, CancellationToken ct = default);

    /// <summary>Tell a newly-added customer which dedicated account to pay into and how much. The
    /// sender's display name is the merchant's business/brand name (<paramref name="businessName"/>).</summary>
    Task SendCustomerAccountDetailsAsync(
        string toEmail, string businessName, string accountNumber, string bankName,
        string accountName, long? expectedAmountKobo, CancellationToken ct = default);

    /// <summary>Dunning notice to a customer: a billing period is due (or overdue). Tells them how
    /// much to pay and into which account. <paramref name="brand"/> is the merchant's display name.</summary>
    Task SendBillingReminderAsync(
        string toEmail, string brand, long amountKobo, DateTimeOffset dueDateUtc,
        string accountNumber, string bankName, bool overdue, CancellationToken ct = default);
}
