namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>A labelled choice for a dashboard header selector.</summary>
public sealed record DashboardOption<T>(string Label, T Value);

/// <summary>Header selector choices, matching 1.3.3 <c>settings.py</c> METRICS / RANGE_OPTIONS / INTERVAL_OPTIONS.</summary>
public static class DashboardOptions
{
    public static IReadOnlyList<DashboardOption<string>> Metrics { get; } =
    [
        new("Total tokens", "Total tokens"),
        new("Output tokens", "Output tokens"),
        new("Equivalent value", "Equivalent value"),
    ];

    public static IReadOnlyList<DashboardOption<int>> Ranges { get; } =
    [
        new("7 days", 7),
        new("14 days", 14),
        new("30 days", 30),
        new("90 days", 90),
    ];

    public static IReadOnlyList<DashboardOption<int>> Intervals { get; } =
    [
        new("Every 30 seconds", 30),
        new("Every minute", 60),
        new("Every 3 minutes · default", 180),
        new("Every 5 minutes", 300),
        new("Every 10 minutes", 600),
        new("Every 30 minutes", 1800),
        new("Manual only", 0),
    ];

    /// <summary>The option whose value matches, or the option at <paramref name="fallbackIndex"/> when none does.</summary>
    public static DashboardOption<T> Find<T>(IReadOnlyList<DashboardOption<T>> options, T value, int fallbackIndex)
        where T : notnull
        => options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? options[fallbackIndex];
}
