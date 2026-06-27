namespace Xental.Domain.Common;

/// <summary>
/// Base type for all domain entities. Carries the identity and common
/// auditing fields that every persisted entity in the system shares.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
