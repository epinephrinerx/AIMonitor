using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>Menu rows and tooltip wording, kept free of WinForms so they can be tested (1.3.3 <c>tray.py</c>).</summary>
public static class TrayText
{
    /// <summary>NotifyIcon.Text throws for anything of 128 characters or more.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>"Session · 5-hour window — 12% · resets in 2h 5m (17:50)".</summary>
    public static string QuotaLine(MeterDisplayItem meter, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(meter);
        var value = meter.HasPercent ? $"{meter.Value:F0}%" : (string.IsNullOrEmpty(meter.Detail) ? "—" : meter.Detail);
        var name = string.IsNullOrEmpty(meter.Subtitle) ? meter.Title : $"{meter.Title} · {meter.Subtitle}";
        return $"{name} — {value} · {When(meter, now, timeZone)}";
    }

    public static string Header(TrayReading reading) =>
        reading.IsStale ? $"{reading.ProviderName}  (last refresh failed)" : reading.ProviderName;

    /// <summary>The multi-line tooltip for the service the icon currently shows.</summary>
    public static string Tooltip(TrayReading reading, int position, int total, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var meter = reading.IconMeter;
        if (meter is null)
        {
            return "AI Usage Monitor — no quota data yet";
        }

        var lines = new List<string>
        {
            string.IsNullOrEmpty(meter.Subtitle)
                ? $"{reading.ProviderName} — {meter.Title}"
                : $"{reading.ProviderName} — {meter.Title} ({meter.Subtitle})",
            $"{meter.Value:F0}% · {SeverityLabels.Describe(meter.Severity).Label}",
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

        return Fit(lines);
    }

    /// <summary>Drops trailing lines until the tooltip fits the shell's limit; truncates a lone long line.</summary>
    public static string Fit(IReadOnlyList<string> lines)
    {
        var count = lines.Count;
        while (count > 1 && string.Join("\n", lines.Take(count)).Length > MaxTooltipLength)
        {
            count--;
        }

        var text = string.Join("\n", lines.Take(count));
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    private static string When(MeterDisplayItem meter, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var display = ResetDisplayFormatter.Format(meter.ResetsAt, now, timeZone);
        if (display is not null && display.Countdown != "now")
        {
            return $"resets in {display.Countdown} ({display.LocalTime})";
        }

        return string.IsNullOrEmpty(meter.LockedReason) ? "no reset scheduled" : meter.LockedReason;
    }
}
