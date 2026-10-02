using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// Applies appearance settings live to UI components (theme callback, widget, and dashboard resizing).
/// Conforms to PAR-027 and 1.3.3 parity:
/// - Invokes theme callback on first apply and when theme changes thereafter.
/// - Forwards settings to WidgetViewModel on every apply.
/// - Invokes dashboard resize callback when dimensions change (including back to (0, 0)).
/// </summary>
public sealed class AppearanceApplier
{
    private readonly Action<string> _applyTheme;
    private readonly WidgetViewModel? _widget;
    private readonly Action<int, int> _resizeDashboard;

    private string? _lastAppliedTheme;
    private bool _hasAppliedTheme;
    private (int Width, int Height) _lastWindowSize;

    public AppearanceApplier(
        Action<string> applyTheme,
        WidgetViewModel? widget,
        Action<int, int> resizeDashboard,
        int initialWidth,
        int initialHeight)
    {
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _widget = widget;
        _resizeDashboard = resizeDashboard ?? throw new ArgumentNullException(nameof(resizeDashboard));
        _lastWindowSize = (initialWidth, initialHeight);
    }

    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!_hasAppliedTheme || !string.Equals(_lastAppliedTheme, settings.Theme, StringComparison.Ordinal))
        {
            _applyTheme(settings.Theme);
            _lastAppliedTheme = settings.Theme;
            _hasAppliedTheme = true;
        }

        _widget?.ApplySettings(settings);

        var currentSize = (settings.DashboardWidth, settings.DashboardHeight);
        if (_lastWindowSize != currentSize)
        {
            _lastWindowSize = currentSize;
            _resizeDashboard(settings.DashboardWidth, settings.DashboardHeight);
        }
    }
}
