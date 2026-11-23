using System.IO;
using System.Text;
using System.Text.Json;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Display;

/// <summary>
/// Turns the tray readings into one line of the ESP32 display protocol v1 (R-022; the protocol document lives with
/// the firmware in <c>docs/PROTOCOL.md</c>). Only what the tray already shows leaves the app: service name, window
/// name, percentage, severity, reset time and the stale/error/lock state. Never accounts or credentials.
/// </summary>
internal static class DisplayFrame
{
    public const int ProtocolVersion = 1;
    public const int MaxProviders = 6;
    public const int MaxWindows = 8;

    // The firmware copies each string into a fixed buffer; these are the buffer sizes minus the terminator.
    internal const int MaxIdLength = 15;
    internal const int MaxNameLength = 19;
    internal const int MaxErrorLength = 47;
    internal const int MaxLabelLength = 23;
    internal const int MaxLockLength = 31;

    /// <summary>The firmware reads lines of at most 4096 bytes including the newline; keep a margin.</summary>
    public const int MaxLineLength = 4000;

    /// <summary>
    /// Builds the frame for <paramref name="now"/>; <paramref name="utcOffset"/> is the PC's local offset. Real
    /// readings are far below <see cref="MaxLineLength"/>; if they ever are not, later windows of each service are
    /// left out until the line fits, so the display always gets a frame it can read.
    /// </summary>
    public static string Build(IReadOnlyList<TrayReading> readings, DateTimeOffset now, TimeSpan utcOffset)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var line = Build(readings, now, utcOffset, MaxWindows);
        for (var cap = MaxWindows - 1; line.Length > MaxLineLength && cap >= 0; cap--)
        {
            line = Build(readings, now, utcOffset, cap);
        }

        return line;
    }

    private static string Build(IReadOnlyList<TrayReading> readings, DateTimeOffset now, TimeSpan utcOffset, int windowCap)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", ProtocolVersion);
            json.WriteNumber("now", now.ToUnixTimeSeconds());
            json.WriteNumber("tz", (int)Math.Round(utcOffset.TotalMinutes));
            json.WriteStartArray("p");
            foreach (var reading in readings.Take(MaxProviders))
            {
                WriteProvider(json, reading, windowCap);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteProvider(Utf8JsonWriter json, TrayReading reading, int windowCap)
    {
        json.WriteStartObject();
        json.WriteString("id", Fit(reading.ProviderId, MaxIdLength));
        json.WriteString("name", Fit(reading.ProviderName, MaxNameLength));
        json.WriteBoolean("stale", reading.IsStale);
        // A service with no windows at all says why (its tab status, e.g. "Not connected").
        json.WriteString("err", reading.Windows.Count == 0 ? Fit(reading.Detail, MaxErrorLength) : "");
        json.WriteStartArray("w");
        foreach (var window in reading.Windows.Take(windowCap))
        {
            WriteWindow(json, window);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteWindow(Utf8JsonWriter json, MeterDisplayItem window)
    {
        json.WriteStartObject();
        json.WriteString("t", Label(window.Title, window.Subtitle));
        if (window.HasPercent)
        {
            json.WriteNumber("pct", Math.Round(Math.Max(0.0, window.Value), 1));
        }
        else
        {
            // No denominator from the service: the display shows "--", never an invented 0.
            json.WriteNull("pct");
        }

        json.WriteNumber("sev", (int)window.Severity);
        json.WriteNumber("reset", window.ResetsAt is DateTimeOffset reset ? Math.Max(0, reset.ToUnixTimeSeconds()) : 0);
        json.WriteString("lock", Fit(window.LockedReason ?? "", MaxLockLength));
        json.WriteEndObject();
    }

    /// <summary>
    /// A short name that tells windows apart. Several windows share a title (every Claude weekly window is
    /// "Weekly"), so the subtitle is folded in when it names a scope: "Weekly" + "Fable only" → "Weekly Fable".
    /// Subtitles that only describe the window ("5-hour window", "All models", "Codex quota") are dropped.
    /// </summary>
    internal static string Label(string title, string subtitle)
    {
        var name = (title ?? "").Replace(" · ", " ", StringComparison.Ordinal).Trim();
        var detail = (subtitle ?? "").Trim();
        if (detail.EndsWith(" only", StringComparison.OrdinalIgnoreCase))
        {
            name = $"{name} {detail[..^" only".Length].Trim()}";
        }
        else if (detail.Length > 0 && !IsDescriptiveSubtitle(detail))
        {
            name = $"{name} {detail}";
        }

        return Fit(name, MaxLabelLength);
    }

    private static bool IsDescriptiveSubtitle(string subtitle) =>
        subtitle.EndsWith("window", StringComparison.OrdinalIgnoreCase)
        || subtitle.Equals("All models", StringComparison.OrdinalIgnoreCase)
        || subtitle.Equals("Codex quota", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The display's fonts are ASCII only: common typography is mapped to ASCII, anything else becomes '?'. The
    /// result is cut to <paramref name="maxLength"/> characters (one byte each, so it fits the firmware buffer).
    /// </summary>
    internal static string Fit(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            builder.Append(c switch
            {
                >= ' ' and <= '~' => c,
                '·' or '–' or '—' or '−' => '-',
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '…' => '.',
                '\t' or ' ' => ' ',
                _ when char.IsControl(c) => ' ',
                _ => '?',
            });
        }

        return builder.ToString().TrimEnd();
    }
}
