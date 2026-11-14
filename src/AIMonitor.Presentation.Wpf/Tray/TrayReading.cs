using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>
/// One service as the tray sees it. The icon draws only <see cref="IconMeter"/> (the five-hour window: one tile
/// holds one number); the menu lists every entry of <see cref="Windows"/> (1.3.3 <c>tray.py</c>).
/// </summary>
public sealed record TrayReading(
    string ProviderId,
    string ProviderName,
    string ProviderCode,
    double Percentage,
    Severity Severity,
    string Detail,
    bool HasData = true)
{
    /// <summary>The five-hour meter the icon draws, when the service has one.</summary>
    public MeterDisplayItem? IconMeter { get; init; }

    /// <summary>Every window the service reported, in reading order, for the menu.</summary>
    public IReadOnlyList<MeterDisplayItem> Windows { get; init; } = [];

    /// <summary>True when the last refresh returned nothing and these are the previous readings.</summary>
    public bool IsStale { get; init; }
}
