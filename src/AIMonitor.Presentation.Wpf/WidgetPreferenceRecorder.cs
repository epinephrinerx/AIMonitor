using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// The one place widget-menu choices (rotation, always on top, opacity) are written to settings, so the
/// window code-behind never touches the settings store directly (see WindowStructuralContractTests).
/// </summary>
internal static class WidgetPreferenceRecorder
{
    public static Task SetRotationAsync(SettingsSession session, bool enabled) =>
        session.UpdateAsync(s => s with { WidgetRotationEnabled = enabled });

    public static Task SetAlwaysOnTopAsync(SettingsSession session, bool enabled) =>
        session.UpdateAsync(s => s with { WidgetAlwaysOnTop = enabled });

    public static Task SetOpacityAsync(SettingsSession session, double opacity) =>
        session.UpdateAsync(s => s with { WidgetOpacity = opacity });
}
