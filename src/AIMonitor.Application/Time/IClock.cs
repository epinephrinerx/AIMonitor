namespace AIMonitor.Application.Time;

/// <summary>
/// The application's sole source of the current instant. Every use case and adapter that needs
/// "now" takes this instead of reading <see cref="DateTimeOffset.UtcNow"/> directly, so tests can
/// supply a fixed instant instead of racing the wall clock.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
