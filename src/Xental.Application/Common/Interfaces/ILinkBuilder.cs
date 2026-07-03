namespace Xental.Application.Common.Interfaces;

/// <summary>
/// Builds the user-facing links embedded in transactional emails, and owns the
/// validity windows for the tokens behind them. Implemented in Infrastructure where
/// the app base URL + auth options live.
/// </summary>
public interface ILinkBuilder
{
    string EmailVerificationLink(string rawToken);
    string PasswordResetLink(string rawToken);
    string TeamInviteLink(string rawToken);

    TimeSpan EmailVerificationTtl { get; }
    TimeSpan PasswordResetTtl { get; }
    TimeSpan TeamInviteTtl { get; }

    /// <summary>How long a dashboard refresh token stays valid.</summary>
    TimeSpan RefreshTokenLifetime { get; }
}
