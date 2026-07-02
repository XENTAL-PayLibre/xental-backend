using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Security;

/// <summary>
/// AES-256-GCM authenticated encryption for at-rest secrets (outbound-webhook signing keys).
/// The key is derived from the environment's JWT signing key (SHA-256), so the ciphertext is
/// useless without that secret. Output = base64(nonce ‖ tag ‖ ciphertext).
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesSecretProtector(IOptions<JwtOptions> jwt)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(jwt.Value.SigningKey));
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, pt, ct, tag);

        var output = new byte[NonceSize + TagSize + ct.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ct, 0, output, NonceSize + TagSize, ct.Length);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string ciphertext)
    {
        var all = Convert.FromBase64String(ciphertext);
        var nonce = all.AsSpan(0, NonceSize);
        var tag = all.AsSpan(NonceSize, TagSize);
        var ct = all.AsSpan(NonceSize + TagSize);
        var pt = new byte[ct.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }
}
