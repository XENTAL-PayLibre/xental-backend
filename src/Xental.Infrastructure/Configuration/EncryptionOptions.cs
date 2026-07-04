namespace Xental.Infrastructure.Configuration;

/// <summary>
/// At-rest encryption key for secrets (outbound-webhook signing keys). Configure a dedicated key so
/// the encryption key is separate from the JWT signing key — otherwise rotating the JWT key would
/// silently make stored secrets undecryptable. When unset, the protector falls back to a JWT-derived
/// key (backward compatible with data encrypted before a dedicated key was configured).
/// </summary>
public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>Dedicated at-rest key (any high-entropy string; hashed to 256 bits). Empty = derive from JWT.</summary>
    public string Key { get; set; } = string.Empty;
}
