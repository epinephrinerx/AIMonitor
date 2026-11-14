namespace AIMonitor.Presentation.Avalonia;

/// <summary>
/// The Windows shortcuts in their macOS spelling (PAR-032): F5 → ⌘R, Ctrl+L → ⌘L, Ctrl+P → ⌘P,
/// Ctrl+W → ⇧⌘W (plain ⌘W closes a window on macOS), F1 → ⇧⌘?, plus ⌘, for Settings and ⌘K for
/// Connections. They sit on the application menu, so they work from the widget too.
/// </summary>
internal static class AppShortcuts
{
    public const string Refresh = "Cmd+R";
    public const string Connections = "Cmd+K";
    public const string UsageLog = "Cmd+L";
    public const string PrintUsageLog = "Cmd+P";
    public const string Settings = "Cmd+OemComma";
    public const string Dashboard = "Shift+Cmd+D";
    public const string Widget = "Shift+Cmd+W";
    public const string About = "Shift+Cmd+OemQuestion";

    public static IReadOnlyList<string> All { get; } =
        [Refresh, Connections, UsageLog, PrintUsageLog, Settings, Dashboard, Widget, About];
}
