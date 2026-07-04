using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Configuration;

namespace Xental.Infrastructure.Security;

/// <summary>
/// AES-256-GCM authenticated encryption for at-rest secrets (outbound-webhook signing keys). Uses a
/// dedicated encryption key when configured (separate from the JWT signing key, so rotating JWT does
/// not orphan stored secrets); otherwise derives the key from the JWT signing key. Unprotect falls
/// back to the legacy JWT-derived key so data written before a dedicated key was set still decrypts.
/// Output = base64(nonce ‖ tag ‖ ciphertext).
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _primaryKey;
    private readonly byte[] _legacyKey;

    public AesSecretProtector(IOptions<JwtOptions> jwt, IOptions<EncryptionOptions> encryption)
    {
        _legacyKey = SHA256.HashData(Encoding.UTF8.GetBytes(jwt.Value.SigningKey));
        var dedicated = encryption.Value.Key;
        _primaryKey = string.IsNullOrWhiteSpace(dedicated) ? _legacyKey : SHA256.HashData(Encoding.UTF8.GetBytes(dedicated));
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_primaryKey, TagSize);
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

        try { return Decrypt(_primaryKey, nonce, tag, ct); }
        catch (CryptographicException) when (!ReferenceEquals(_primaryKey, _legacyKey))
        {
            // Written before a dedicated key was configured — decrypt with the legacy JWT-derived key.
            return Decrypt(_legacyKey, nonce, tag, ct);
        }
    }

    private static string Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> ct)
    {
        var pt = new byte[ct.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }
}
