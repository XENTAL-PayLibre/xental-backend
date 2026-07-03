namespace Xental.Application.Common.Interfaces;

/// <summary>Time-based one-time passwords (RFC 6238) for admin MFA.</summary>
public interface ITotpService
{
    /// <summary>A fresh base32 shared secret to enroll an authenticator app.</summary>
    string GenerateSecret();

    /// <summary>Verify a 6-digit code against the secret (±1 time step tolerance).</summary>
    bool Verify(string base32Secret, string code);

    /// <summary>otpauth:// URI for the enrollment QR code.</summary>
    string BuildOtpAuthUri(string base32Secret, string accountEmail, string issuer);
}
