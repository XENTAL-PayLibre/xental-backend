using System.Security.Cryptography;
using System.Text;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Security;

/// <summary>
/// Deterministic SHA-256 hash for high-entropy single-use tokens (magic links).
/// The tokens are random, so a fast digest is sufficient and enables lookup-by-hash.
/// </summary>
public sealed class Sha256TokenHasher : ITokenHasher
{
    public string Hash(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken ?? string.Empty));
        return Convert.ToHexString(bytes);
    }
}
