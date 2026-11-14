namespace AIMonitor.Application.Settings;

/// <summary>
/// Pure policy functions governing system tray visibility, minimization, and hidden startup behavior.
/// </summary>
public static class TrayPolicy
{
    /// <summary>
    /// Determines whether the system tray icon should be active and visible.
    /// </summary>
    /// <param name="settings">The application settings to evaluate.</param>
    /// <returns><c>true</c> if tray icon is enabled in settings; otherwise, <c>false</c>.</returns>
    public static bool ShouldShowTray(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ShowTrayIcon;
    }

    /// <summary>
    /// Determines whether closing the main window should hide it to the system tray rather than exiting.
    /// Returns <c>true</c> only when both <see cref="AppSettings.MinimizeToTray"/> and <see cref="AppSettings.ShowTrayIcon"/> are enabled,
    /// ensuring the window is never hidden with no tray icon to restore it.
    /// </summary>
    /// <param name="settings">The application settings to evaluate.</param>
    /// <returns><c>true</c> if the window should hide on close; otherwise, <c>false</c>.</returns>
    public static bool ShouldHideOnClose(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MinimizeToTray && settings.ShowTrayIcon;
    }

    /// <summary>
    /// Determines whether the application should start hidden (e.g. via <c>--minimized</c>, <c>--tray</c>, or <c>-m</c>).
    /// Starting hidden is permitted only when a system tray icon exists to restore the application.
    /// </summary>
    /// <param name="settings">The application settings to evaluate.</param>
    /// <param name="startMinimizedRequested">Whether minimized/tray startup was requested via command-line arguments.</param>
    /// <returns><c>true</c> if the application should start hidden; otherwise, <c>false</c>.</returns>
    public static bool ShouldStartHidden(AppSettings settings, bool startMinimizedRequested)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return startMinimizedRequested && settings.ShowTrayIcon;
    }
}
