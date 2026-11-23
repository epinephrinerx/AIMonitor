using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>One entry of the ESP32 display port list in Settings (R-022).</summary>
public sealed record DisplayPortOption(string Label, string Value)
{
    public const string AutoLabel = "Find automatically";

    /// <summary>
    /// "Find automatically" first, then the ports present now. A saved port that is unplugged at the moment is still
    /// listed (marked) so opening Settings never silently changes the choice.
    /// </summary>
    public static IReadOnlyList<DisplayPortOption> Build(IReadOnlyList<string>? presentPorts, string? savedPort)
    {
        var options = new List<DisplayPortOption> { new(AutoLabel, "auto") };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto" };
        foreach (var port in presentPorts ?? [])
        {
            var value = AppSettings.NormalizeDisplayPort(port);
            if (seen.Add(value))
            {
                options.Add(new DisplayPortOption(value, value));
            }
        }

        var saved = AppSettings.NormalizeDisplayPort(savedPort);
        if (seen.Add(saved))
        {
            options.Add(new DisplayPortOption($"{saved} (not connected)", saved));
        }

        return options;
    }
}
