namespace Xental.Application.Common.Interfaces;

/// <summary>Sends transactional email (magic-link verification, password reset, etc.).</summary>
public interface IEmailSender
{
    Task SendEmailVerificationAsync(string toEmail, string verifyLink, CancellationToken ct = default);
    Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken ct = default);
}
