namespace Xental.Application.Common.Interfaces;

/// <summary>Hashes and verifies user passwords (bcrypt). Verification is constant-time.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Constant-time verify. A null/empty stored hash still runs a dummy
    /// comparison so unknown accounts can't be found by timing.</summary>
    bool Verify(string password, string? hash);
}
