using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.MenuBar;

/// <summary>
/// One service as the menu bar sees it. The icon draws <see cref="IconMeter"/> only — one tile holds
/// one number — while the menu lists every entry of <see cref="Windows"/>.
/// </summary>
public sealed record MenuBarReading(
    string ProviderId,
    string ProviderName,
    MeterViewModel? IconMeter,
    IReadOnlyList<MeterViewModel> Windows,
    bool IsStale)
{
    public double? Percent => IconMeter?.Percent;

    public Severity Severity => IconMeter?.Severity ?? Severity.Normal;

    public bool HasData => IconMeter is not null;
}

/// <summary>Turns the dashboard's provider view models into what the menu bar shows.</summary>
public static class MenuBarReadings
{
    /// <summary>
    /// A service whose refresh returned nothing keeps the windows it last reported, marked stale,
    /// rather than vanishing from the menu bar and leaving the user with no reading at all.
    /// </summary>
    /// <param name="lastGood">Per-service cache of the last meters that actually arrived; updated here.</param>
    public static IReadOnlyList<MenuBarReading> Build(
        IEnumerable<ProviderViewModel> providers,
        IDictionary<string, IReadOnlyList<MeterViewModel>> lastGood)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(lastGood);

        var readings = new List<MenuBarReading>();
        foreach (var provider in providers)
        {
            IReadOnlyList<MeterViewModel> windows;
            var stale = false;
            if (provider.Configured && provider.Meters.Count > 0)
            {
                windows = [.. provider.Meters];
                lastGood[provider.ProviderId] = windows;
            }
            else
            {
                windows = lastGood.TryGetValue(provider.ProviderId, out var kept) ? kept : [];
                stale = windows.Count > 0;
            }

            readings.Add(new MenuBarReading(
                provider.ProviderId,
                provider.Title,
                windows.FirstOrDefault(meter => meter.IsFiveHour),
                windows,
                stale));
        }

        return readings;
    }
}
