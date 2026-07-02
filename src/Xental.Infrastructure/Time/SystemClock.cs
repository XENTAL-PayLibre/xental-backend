using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
