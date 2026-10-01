using System.IO;
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
    private readonly SettingsSession _session;
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
        SettingsSession session,
        IStartupRegistrar startupRegistrar)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _startupRegistrar = startupRegistrar ?? throw new ArgumentNullException(nameof(startupRegistrar));
        _originalSettings = session.Current;

        _theme = _originalSettings.Theme;
        _startWithWindows = _originalSettings.StartWithWindows;
        _minimizeToTray = _originalSettings.MinimizeToTray;
        _showTrayIcon = _originalSettings.ShowTrayIcon;
        _refreshIntervalSeconds = _originalSettings.RefreshIntervalSeconds;
        _widgetOpacity = _originalSettings.WidgetOpacity;
        _widgetAlwaysOnTop = _originalSettings.WidgetAlwaysOnTop;
        _chartRangeDays = _originalSettings.ChartRangeDays;

        SaveCommand = new RelayCommand(async () => await ExecuteSaveCommandAsync());
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
    public event Action<string>? SaveFailed;

    private async Task ExecuteSaveCommandAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = ex is UnauthorizedAccessException
                ? "Failed to save settings: Access denied."
                : "Failed to save settings due to an I/O error.";
            SaveFailed?.Invoke(message);
        }
    }

    public async Task SaveAsync()
    {
        await _session.UpdateAsync(s => s with
        {
            Theme = Theme,
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            ShowTrayIcon = ShowTrayIcon,
            RefreshIntervalSeconds = RefreshIntervalSeconds,
            WidgetOpacity = WidgetOpacity,
            WidgetAlwaysOnTop = WidgetAlwaysOnTop,
            ChartRangeDays = ChartRangeDays
        }).ConfigureAwait(true);

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
