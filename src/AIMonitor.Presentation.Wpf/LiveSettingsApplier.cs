using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// Applies application settings live to UI components (refresh interval, widget, and system tray).
/// Decouples live settings orchestration from WPF application lifecycle for direct unit testability.
/// </summary>
public sealed class LiveSettingsApplier
{
    private readonly WidgetViewModel? _widgetViewModel;
    private readonly MainWindowViewModel _mainViewModel;
    private readonly Func<ITrayHost> _createTray;
    private readonly Action<TimeSpan> _setRefreshInterval;
    private readonly Func<bool> _isAnyWindowVisible;
    private readonly Action _showDashboard;
    private TimeSpan? _lastAppliedRefreshInterval;
    private ITrayHost? _currentTray;

    public LiveSettingsApplier(
        WidgetViewModel? widgetViewModel,
        MainWindowViewModel mainViewModel,
        Func<ITrayHost> createTray,
        Action<TimeSpan> setRefreshInterval,
        Func<bool> isAnyWindowVisible,
        Action showDashboard)
    {
        _widgetViewModel = widgetViewModel;
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _createTray = createTray ?? throw new ArgumentNullException(nameof(createTray));
        _setRefreshInterval = setRefreshInterval ?? throw new ArgumentNullException(nameof(setRefreshInterval));
        _isAnyWindowVisible = isAnyWindowVisible ?? throw new ArgumentNullException(nameof(isAnyWindowVisible));
        _showDashboard = showDashboard ?? throw new ArgumentNullException(nameof(showDashboard));
    }

    /// <summary>
    /// Gets the currently active system tray host, or <c>null</c> if tray is disabled.
    /// </summary>
    public ITrayHost? CurrentTray => _currentTray;

    /// <summary>
    /// Applies application settings to the refresh interval, widget view model, and tray lifecycle.
    /// </summary>
    /// <param name="settings">The application settings to apply.</param>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var refreshInterval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        if (_lastAppliedRefreshInterval != refreshInterval)
        {
            _setRefreshInterval(refreshInterval);
            _lastAppliedRefreshInterval = refreshInterval;
        }

        _widgetViewModel?.ApplySettings(settings);

        var shouldShowTray = TrayPolicy.ShouldShowTray(settings);
        if (shouldShowTray && _currentTray is null)
        {
            _currentTray = _createTray();
            _mainViewModel.AttachTray(_currentTray);
        }
        else if (!shouldShowTray && _currentTray is not null)
        {
            _mainViewModel.AttachTray(null);
            _currentTray.Dispose();
            _currentTray = null;

            if (!_isAnyWindowVisible())
            {
                _showDashboard();
            }
        }
    }

    /// <summary>
    /// Shuts down the active tray host by detaching it from the view model and disposing it.
    /// This method is idempotent.
    /// </summary>
    public void Shutdown()
    {
        if (_currentTray is null)
        {
            return;
        }

        _mainViewModel.AttachTray(null);
        _currentTray.Dispose();
        _currentTray = null;
    }
}
