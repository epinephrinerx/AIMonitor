using System.Globalization;

namespace AIMonitor.Domain;

/// <summary>A reset timestamp rendered two ways: a coarse countdown and a local clock reading.</summary>
public sealed record ResetDisplay(string Countdown, string LocalTime);

/// <summary>
/// Formats a meter's reset timestamp against an explicit reference instant and time zone, so the
/// result never depends on the wall clock or the machine's own time zone.
/// </summary>
public static class ResetDisplayFormatter
{
    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="resetsAt"/> is absent; otherwise a
    /// countdown ("2d 8h", "1h 12m", "4m", "under a minute", "now") and a local clock string
    /// ("HH:mm" for today in <paramref name="timeZone"/>, otherwise "MMM d, HH:mm").
    /// </summary>
    public static ResetDisplay? Format(DateTimeOffset? resetsAt, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        if (resetsAt is not DateTimeOffset value)
        {
            return null;
        }

        var countdown = FormatCountdown(value - now);
        var localMoment = TimeZoneInfo.ConvertTime(value, timeZone);
        var today = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        var localTime = localMoment.Date == today
            ? localMoment.ToString("HH:mm", CultureInfo.InvariantCulture)
            : localMoment.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

        return new ResetDisplay(countdown, localTime);
    }

    /// <summary>
    /// Coarse countdown: elapsed is "now", under a minute is "under a minute", then minutes,
    /// hours (with minutes), and days (with hours).
    /// </summary>
    public static string FormatCountdown(TimeSpan delta)
    {
        var totalSeconds = (long)delta.TotalSeconds;
        if (totalSeconds <= 0)
        {
            return "now";
        }

        var days = totalSeconds / 86_400;
        var remainder = totalSeconds % 86_400;
        var hours = remainder / 3_600;
        remainder %= 3_600;
        var minutes = remainder / 60;

        if (days > 0)
        {
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }

        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        if (minutes > 0)
        {
            return $"{minutes}m";
        }

        return "under a minute";
    }
}
