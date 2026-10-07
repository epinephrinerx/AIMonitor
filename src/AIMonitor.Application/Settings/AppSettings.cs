namespace AIMonitor.Application.Settings;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool StartWithWindows { get; init; } = true;

    public bool MinimizeToTray { get; init; } = true;

    public bool ShowTrayIcon { get; init; } = true;

    /// <summary>Set once the "Still watching" balloon has been shown, so it appears only the first time the window parks in the tray.</summary>
    public bool TrayHintShown { get; init; }

    public string Theme { get; init; } = "system";

    public int DashboardWidth { get; init; } = 1120;

    public int DashboardHeight { get; init; } = 820;

    public int RefreshIntervalSeconds { get; init; } = 180;

    public double WidgetOpacity { get; init; } = 0.92;

    public bool WidgetAlwaysOnTop { get; init; } = true;

    public bool WidgetRotationEnabled { get; init; } = true;

    public bool ShowConnectionsAtStartup { get; init; } = true;

    public int ChartRangeDays { get; init; } = 14;

    public string ChartMetric { get; init; } = "Total tokens";

    public string ActiveProvider { get; init; } = "claude";

    public IReadOnlyDictionary<string, ProviderPreference> Providers { get; init; } =
        new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opaque Qt geometry retained during the forward-only migration. A later WPF geometry
    /// adapter may consume recognized values; unknown encodings remain preserved, not invented.
    /// </summary>
    public IReadOnlyDictionary<string, string> LegacyGeometry { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public AppSettings Normalize()
    {
        var theme = Theme is "system" or "light" or "dark" ? Theme : "system";
        var metric = ChartMetric is "Total tokens" or "Output tokens" or "Equivalent value"
            ? ChartMetric
            : "Total tokens";
        var range = ChartRangeDays is 7 or 14 or 30 or 90 ? ChartRangeDays : 14;
        var rememberSize = DashboardWidth == 0 && DashboardHeight == 0;
        var activeProvider = ActiveProvider is "claude" or "openai" or "gemini" ? ActiveProvider : "claude";

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Theme = theme,
            DashboardWidth = rememberSize ? 0 : Math.Max(760, DashboardWidth),
            DashboardHeight = rememberSize ? 0 : Math.Max(560, DashboardHeight),
            // 0 means "Manual only" (1.3.3 INTERVAL_OPTIONS); anything else is clamped.
            RefreshIntervalSeconds = RefreshIntervalSeconds == 0 ? 0 : Math.Clamp(RefreshIntervalSeconds, 30, 86_400),
            WidgetOpacity = Math.Clamp(WidgetOpacity, 0.25, 1.0),
            ChartRangeDays = range,
            ChartMetric = metric,
            ActiveProvider = activeProvider,
            Providers = Providers is null
                ? new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase)
                : Providers
                    .Where(static pair => pair.Value is not null)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            LegacyGeometry = LegacyGeometry is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : LegacyGeometry
                    .Where(static pair => pair.Value is not null)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        };
    }
}

public sealed record ProviderPreference(bool Enabled = true, string Extra = "");
