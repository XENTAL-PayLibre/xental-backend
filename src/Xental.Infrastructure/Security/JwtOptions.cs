namespace Xental.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "xental";
    public string Audience { get; set; } = "xental-api";

    /// <summary>HMAC signing key (>= 32 bytes). Supplied via secret/env in real envs.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenLifetimeSeconds { get; set; } = 3600;
}
