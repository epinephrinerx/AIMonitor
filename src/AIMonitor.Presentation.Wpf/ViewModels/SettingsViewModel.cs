using System.Windows.Input;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.Theme;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// ViewModel for the Settings dialog with live preview and atomic save.
/// Conforms to PAR-026 and PAR-027.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly IStartupRegistrar _startupRegistrar;
    private readonly AppSettings _originalSettings;

    private string _theme;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _showTrayIcon;
    private int _refreshIntervalSeconds;
    private double _widgetOpacity;
    private bool _widgetAlwaysOnTop;
    private int _chartRangeDays;

    public SettingsViewModel(
        AppSettings currentSettings,
        ISettingsStore settingsStore,
        IStartupRegistrar startupRegistrar)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _startupRegistrar = startupRegistrar ?? throw new ArgumentNullException(nameof(startupRegistrar));
        _originalSettings = currentSettings ?? throw new ArgumentNullException(nameof(currentSettings));

        _theme = currentSettings.Theme;
        _startWithWindows = currentSettings.StartWithWindows;
        _minimizeToTray = currentSettings.MinimizeToTray;
        _showTrayIcon = currentSettings.ShowTrayIcon;
        _refreshIntervalSeconds = currentSettings.RefreshIntervalSeconds;
        _widgetOpacity = currentSettings.WidgetOpacity;
        _widgetAlwaysOnTop = currentSettings.WidgetAlwaysOnTop;
        _chartRangeDays = currentSettings.ChartRangeDays;

        SaveCommand = new RelayCommand(async () => await SaveAsync());
        CancelCommand = new RelayCommand(Cancel);
    }

    public string Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                // Live preview theme (PAR-027)
                ThemeManager.Instance.ApplyTheme(value);
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set => SetProperty(ref _showTrayIcon, value);
    }

    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set => SetProperty(ref _refreshIntervalSeconds, value);
    }

    public double WidgetOpacity
    {
        get => _widgetOpacity;
        set => SetProperty(ref _widgetOpacity, value);
    }

    public bool WidgetAlwaysOnTop
    {
        get => _widgetAlwaysOnTop;
        set => SetProperty(ref _widgetAlwaysOnTop, value);
    }

    public int ChartRangeDays
    {
        get => _chartRangeDays;
        set => SetProperty(ref _chartRangeDays, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool>? RequestClose;

    public async Task SaveAsync()
    {
        var updated = (_originalSettings with
        {
            Theme = Theme,
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            ShowTrayIcon = ShowTrayIcon,
            RefreshIntervalSeconds = RefreshIntervalSeconds,
            WidgetOpacity = WidgetOpacity,
            WidgetAlwaysOnTop = WidgetAlwaysOnTop,
            ChartRangeDays = ChartRangeDays
        }).Normalize();

        await _settingsStore.SaveAsync(updated).ConfigureAwait(true);

        // Update Windows startup entry only on Save (PAR-024, PAR-027)
        try
        {
            if (StartWithWindows)
            {
                var exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath))
                {
                    _startupRegistrar.Register(exePath, "--tray");
                }
            }
            else
            {
                _startupRegistrar.Unregister();
            }
        }
        catch
        {
            // Non-fatal if startup registration fails (e.g. registry restricted)
        }

        RequestClose?.Invoke(true);
    }

    public void Cancel()
    {
        // Revert live preview back to original settings (PAR-027)
        ThemeManager.Instance.ApplyTheme(_originalSettings.Theme);
        RequestClose?.Invoke(false);
    }
}
