using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>Turns the provider tabs into tray readings, the way 1.3.3 <c>TrayController.set_snapshots</c> does.</summary>
public static class TrayReadings
{
    /// <summary>
    /// The icon draws the five-hour window only; the menu lists every window. A service whose refresh
    /// returned nothing keeps its previous windows, marked stale, rather than vanishing from the tray.
    /// </summary>
    /// <param name="lastGood">Per-service cache of the last meters that actually arrived; updated here.</param>
    public static IReadOnlyList<TrayReading> Build(
        IEnumerable<ProviderTabViewModel> tabs,
        IDictionary<string, IReadOnlyList<MeterDisplayItem>> lastGood)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(lastGood);

        var readings = new List<TrayReading>();
        foreach (var tab in tabs)
        {
            IReadOnlyList<MeterDisplayItem> windows;
            var stale = false;
            if (tab.IsConfigured && tab.Meters.Count > 0)
            {
                windows = tab.Meters;
                lastGood[tab.ProviderId] = windows;
            }
            else
            {
                windows = lastGood.TryGetValue(tab.ProviderId, out var kept) ? kept : [];
                stale = windows.Count > 0;
            }

            var icon = windows.FirstOrDefault(m => m.IsFiveHour);
            readings.Add(new TrayReading(
                tab.ProviderId,
                tab.DisplayName,
                CodeFor(tab.ProviderId),
                icon?.Value ?? 0.0,
                icon?.Severity ?? Domain.Severity.Normal,
                icon?.Title ?? tab.Status,
                icon is not null)
            {
                IconMeter = icon,
                Windows = windows,
                IsStale = stale,
            });
        }

        return readings;
    }

    private static string CodeFor(string providerId) => providerId switch
    {
        "claude" => "CL",
        "openai" => "OA",
        "gemini" => "GE",
        _ => "AI",
    };
}
