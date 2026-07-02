namespace Xental.Application.Common.Interfaces;

/// <summary>Hashes and verifies client secrets. Verification must be constant-time.</summary>
public interface ISecretHasher
{
    string Hash(string secret);
    bool Verify(string secret, string hash);
}
