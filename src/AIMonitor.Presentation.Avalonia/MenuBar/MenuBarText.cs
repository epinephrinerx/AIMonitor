using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.MenuBar;

/// <summary>
/// The wording of every menu row and of the hover text. Pure string work with no toolkit types, so
/// each rule here is testable without a menu bar to put it in.
/// </summary>
public static class MenuBarText
{
    /// <summary>"Session · 5-hour window — 12% · resets in 2h 5m (17:50)".</summary>
    public static string QuotaLine(MeterViewModel meter, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(timeZone);

        var value = meter.HasPercent
            ? $"{meter.Percent!.Value:F0}%"
            : (meter.Detail.Length == 0 ? "—" : meter.Detail);
        var name = meter.HasSubtitle ? $"{meter.Title} · {meter.Subtitle}" : meter.Title;
        return $"{name} — {value} · {When(meter, now, timeZone)}";
    }

    /// <summary>The service's own row, which says so when its last refresh failed.</summary>
    public static string Header(MenuBarReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.IsStale ? $"{reading.ProviderName}  (last refresh failed)" : reading.ProviderName;
    }

    /// <summary>
    /// The hover text for whichever service the icon is showing. Unlike the Windows tray there is no
    /// 127-character ceiling here, but it is still kept to a few lines: this is a glance, not a page.
    /// </summary>
    public static string Tooltip(MenuBarReading reading, int position, int total, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(timeZone);

        var meter = reading.IconMeter;
        if (meter is null)
        {
            return "AI Usage Monitor — no quota data yet";
        }

        var lines = new List<string>
        {
            meter.HasSubtitle
                ? $"{reading.ProviderName} — {meter.Title} ({meter.Subtitle})"
                : $"{reading.ProviderName} — {meter.Title}",
            $"{meter.Percent!.Value:F0}% · {SeverityLabels.Describe(meter.Severity).Label}",
        };

        var display = ResetDisplayFormatter.Format(meter.ResetsAt, now, timeZone);
        if (display is not null && display.Countdown != "now")
        {
            lines.Add($"resets in {display.Countdown} ({display.LocalTime})");
        }

        if (reading.IsStale)
        {
            lines.Add("last refresh failed — showing the previous reading");
        }

        if (total > 1)
        {
            lines.Add($"{position + 1} of {total} services");
        }

        return string.Join("\n", lines);
    }

    private static string When(MeterViewModel meter, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var display = ResetDisplayFormatter.Format(meter.ResetsAt, now, timeZone);
        if (display is not null && display.Countdown != "now")
        {
            return $"resets in {display.Countdown} ({display.LocalTime})";
        }

        return meter.IsLocked ? meter.LockedReason : "no reset scheduled";
    }
}
