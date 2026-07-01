using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Security;

/// <summary>
/// Password hashing with bcrypt. Verifying a null/empty stored hash still runs a
/// bcrypt comparison against a dummy hash so unknown accounts take the same time.
/// </summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private readonly int _workFactor;
    private readonly string _dummyHash;

    public BcryptPasswordHasher(IOptions<AuthOptions> options)
    {
        _workFactor = Math.Max(12, options.Value.BcryptWorkFactor);
        _dummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-timing-password", _workFactor);
    }

    public string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, _workFactor);

    public bool Verify(string password, string? hash)
    {
        var target = string.IsNullOrEmpty(hash) ? _dummyHash : hash;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password ?? string.Empty, target);
        }
        catch
        {
            return false;
        }
    }
}
