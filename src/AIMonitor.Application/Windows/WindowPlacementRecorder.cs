namespace AIMonitor.Application.Windows;

using AIMonitor.Application.Settings;

/// <summary>
/// Persists window placement updates through a <see cref="SettingsSession"/> into the centralized settings store.
/// </summary>
public static class WindowPlacementRecorder
{
    /// <summary>
    /// Records window placement for the specified display topology into the settings session.
    /// </summary>
    public static Task RecordAsync(
        SettingsSession session,
        string mode,
        WindowPlacement placement,
        IReadOnlyList<DisplayArea> displays,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(displays);

        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
        return session.UpdateAsync(s =>
            WindowGeometryManager.SavePlacement(s, topologyId, mode, placement, now));
    }
}
