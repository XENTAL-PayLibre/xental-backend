namespace Xental.Application.Common.Interfaces;

/// <summary>Abstracts the system clock so time-dependent logic is testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
