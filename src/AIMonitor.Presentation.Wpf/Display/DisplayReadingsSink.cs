using AIMonitor.Application.Display;
using AIMonitor.Presentation.Wpf.Tray;

namespace AIMonitor.Presentation.Wpf.Display;

/// <summary>
/// Receives the same readings as the tray and hands them to the <see cref="DisplayFeed"/> (R-022). Separate from the
/// tray sink on purpose: the tray host also implements <see cref="ITrayNotifier"/>, which a combined sink would hide.
/// </summary>
internal sealed class DisplayReadingsSink : ITrayReadingsSink
{
    private readonly DisplayFeed _feed;
    private readonly Func<DateTimeOffset, TimeSpan> _utcOffset;

    public DisplayReadingsSink(DisplayFeed feed, Func<DateTimeOffset, TimeSpan>? utcOffset = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _utcOffset = utcOffset ?? (static now => TimeZoneInfo.Local.GetUtcOffset(now));
    }

    public void UpdateReadings(IEnumerable<TrayReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var snapshot = readings.ToList();
        _feed.Publish(now => DisplayFrame.Build(snapshot, now, _utcOffset(now)));
    }
}
