using System.Globalization;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>Builds the dashboard footer line the way 1.3.3 <c>_update_status</c> does.</summary>
public static class StatusBarText
{
    /// <summary>
    /// "Updated 12s ago (14:03:22)  ·  auto-refresh every 3 minutes · default  ·  85 MB resident".
    /// </summary>
    public static string Compose(
        DateTimeOffset? lastSuccess,
        DateTimeOffset now,
        int intervalSeconds,
        double? workingSetMb,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var parts = new List<string>();
        if (lastSuccess is not DateTimeOffset last)
        {
            parts.Add("No successful fetch yet");
        }
        else
        {
            var age = now - last;
            var when = age.TotalSeconds < 5 ? "just now" : $"{ResetDisplayFormatter.FormatCountdown(age)} ago";
            var clock = TimeZoneInfo.ConvertTime(last, timeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            parts.Add($"Updated {when} ({clock})");
        }

        parts.Add(intervalSeconds > 0
            ? $"auto-refresh {IntervalLabel(intervalSeconds).ToLowerInvariant()}"
            : "manual refresh");

        if (workingSetMb is > 0)
        {
            parts.Add($"{workingSetMb.Value:F0} MB resident");
        }

        return string.Join("  ·  ", parts);
    }

    private static string IntervalLabel(int seconds) =>
        DashboardOptions.Intervals.FirstOrDefault(o => o.Value == seconds)?.Label
        ?? $"Every {seconds} seconds";
}
