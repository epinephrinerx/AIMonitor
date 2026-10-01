using AIMonitor.Application.Time;

namespace AIMonitor.TestSupport;

/// <summary>Hand-written <see cref="IClock"/> fake: a settable instant, never the wall clock.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }
}
