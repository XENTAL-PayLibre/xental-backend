using System.Security.Cryptography;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 hashing for client secrets. Format: iterations.salt.key
/// (base64). Verification is constant-time. No external dependencies.
/// </summary>
public sealed class Pbkdf2SecretHasher : ISecretHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algo = HashAlgorithmName.SHA256;

    public string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, Algo, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string secret, string hash)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(hash))
            return false;

        var parts = hash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            return false;

        byte[] salt, key;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            key = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var attempt = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, Algo, key.Length);
        return CryptographicOperations.FixedTimeEquals(attempt, key);
    }
}
