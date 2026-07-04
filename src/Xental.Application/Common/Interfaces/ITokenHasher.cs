namespace Xental.Application.Common.Interfaces;

/// <summary>
/// Deterministic hash for high-entropy, single-use tokens (magic links). Unlike
/// password/secret hashing (salted, slow), these tokens are random and looked up by
/// hash, so a fast deterministic digest (SHA-256) is appropriate and required.
/// </summary>
public interface ITokenHasher
{
    string Hash(string rawToken);
}
