using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Time;

/// <summary>
/// Production clock that reads the real system wall clock via DateTimeOffset.UtcNow.
/// </summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
