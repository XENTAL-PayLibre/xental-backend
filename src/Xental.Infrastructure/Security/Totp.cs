using System.Security.Cryptography;
using System.Text;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Security;

/// <summary>
/// RFC 6238 TOTP (HMAC-SHA1, 30-second step, 6 digits) — self-contained, compatible with Google
/// Authenticator / Authy. Used for the admin plane's mandatory second factor.
/// </summary>
public sealed class Totp : ITotpService
{
    private const int Digits = 6;
    private const int StepSeconds = 30;

    public string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public bool Verify(string base32Secret, string code)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
            return false;
        code = code.Trim();
        byte[] key;
        try { key = Base32Decode(base32Secret); } catch { return false; }
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        for (long window = -1; window <= 1; window++)
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Compute(key, counter + window)), Encoding.ASCII.GetBytes(code)))
                return true;
        return false;
    }

    public string BuildOtpAuthUri(string base32Secret, string accountEmail, string issuer) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountEmail)}" +
        $"?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    private static string Compute(byte[] key, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
        var hash = new HMACSHA1(key).ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        return (binary % (int)Math.Pow(10, Digits)).ToString().PadLeft(Digits, '0');
    }

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(value >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in input)
        {
            var idx = Base32Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException("Invalid base32.");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8) { output.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return output.ToArray();
    }
}
