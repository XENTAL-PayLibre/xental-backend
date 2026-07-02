namespace Xental.Application.Common.Interfaces;

/// <summary>Generates cryptographically-secure opaque tokens (client id/secret).</summary>
public interface ITokenGenerator
{
    /// <summary>URL-safe token with the given prefix and byte-strength.</summary>
    string Generate(string prefix, int bytes = 24);
}
