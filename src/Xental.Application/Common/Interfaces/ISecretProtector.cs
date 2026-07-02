namespace Xental.Application.Common.Interfaces;

/// <summary>
/// Reversible encryption for secrets that must be recovered at use time (e.g. a developer's
/// outbound-webhook signing secret, needed to compute the HMAC on each delivery). Implemented
/// with ASP.NET Core Data Protection so the ciphertext at rest is useless without the key.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
