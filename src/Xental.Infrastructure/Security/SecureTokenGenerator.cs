using System.Security.Cryptography;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Security;

/// <summary>Cryptographically-secure, URL-safe opaque tokens with a prefix.</summary>
public sealed class SecureTokenGenerator : ITokenGenerator
{
    public string Generate(string prefix, int bytes = 24)
    {
        var raw = RandomNumberGenerator.GetBytes(bytes);
        var encoded = Convert.ToBase64String(raw)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{prefix}_{encoded}";
    }
}
