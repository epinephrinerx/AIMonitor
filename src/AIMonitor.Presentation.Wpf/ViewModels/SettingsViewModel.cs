using System.IO;
using System.Windows.Input;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// ViewModel for the Settings dialog with live preview, atomic save, and revert on discard/cancel.
/// Conforms to PAR-026, PAR-027, and 1.3.3 parity.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsSession _session;
    private readonly IStartupRegistrar _startupRegistrar;
    private readonly Action<AppSettings>? _applyPreview;
    private readonly AppSettings _entrySettings;

    private string _theme;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _showTrayIcon;
    private int _refreshIntervalSeconds;
    private double _widgetOpacity;
    private bool _widgetAlwaysOnTop;
    private int _chartRangeDays;
    private WindowSizeOption _selectedWindowSize;

    private bool _discarded;
    private bool _saved;
    private bool _isSaving;

    public bool IsSaving
    {
        get => _isSaving;
        private set => SetProperty(ref _isSaving, value);
    }

    public SettingsViewModel(
        SettingsSession session,
        IStartupRegistrar startupRegistrar,
        Action<AppSettings>? applyPreview = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _startupRegistrar = startupRegistrar ?? throw new ArgumentNullException(nameof(startupRegistrar));
        _applyPreview = applyPreview;
        _entrySettings = session.Current;

        _theme = _entrySettings.Theme;
        _startWithWindows = _entrySettings.StartWithWindows;
        _minimizeToTray = _entrySettings.MinimizeToTray;
        _showTrayIcon = _entrySettings.ShowTrayIcon;
        _refreshIntervalSeconds = _entrySettings.RefreshIntervalSeconds;
        _widgetOpacity = Math.Clamp(_entrySettings.WidgetOpacity, 0.25, 1.0);
        _widgetAlwaysOnTop = _entrySettings.WidgetAlwaysOnTop;
        _chartRangeDays = _entrySettings.ChartRangeDays;
        _selectedWindowSize = WindowSizeOption.FromSize(_entrySettings.DashboardWidth, _entrySettings.DashboardHeight);

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
                ApplyPreview();
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
        set
        {
            var clamped = Math.Clamp(value, 0.25, 1.0);
            if (SetProperty(ref _widgetOpacity, clamped))
            {
                OnPropertyChanged(nameof(OpacityPercent));
                ApplyPreview();
            }
        }
    }

    public int OpacityPercent => (int)Math.Round(_widgetOpacity * 100);

    public bool WidgetAlwaysOnTop
    {
        get => _widgetAlwaysOnTop;
        set
        {
            if (SetProperty(ref _widgetAlwaysOnTop, value))
            {
                ApplyPreview();
            }
        }
    }

    public int ChartRangeDays
    {
        get => _chartRangeDays;
        set => SetProperty(ref _chartRangeDays, value);
    }

    public IReadOnlyList<WindowSizeOption> WindowSizeOptions => WindowSizeOption.All;

    public WindowSizeOption SelectedWindowSize
    {
        get => _selectedWindowSize;
        set
        {
            var option = value ?? WindowSizeOption.Standard;
            if (SetProperty(ref _selectedWindowSize, option))
            {
                ApplyPreview();
            }
        }
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool>? RequestClose;
    public event Action<string>? SaveFailed;

    private void ApplyPreview()
    {
        _applyPreview?.Invoke(_session.Current with
        {
            Theme = Theme,
            WidgetOpacity = WidgetOpacity,
            WidgetAlwaysOnTop = WidgetAlwaysOnTop,
            DashboardWidth = SelectedWindowSize.Width,
            DashboardHeight = SelectedWindowSize.Height
        });
    }

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
        IsSaving = true;
        try
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
                ChartRangeDays = ChartRangeDays,
                DashboardWidth = SelectedWindowSize.Width,
                DashboardHeight = SelectedWindowSize.Height
            }).ConfigureAwait(true);

            _saved = true;

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
        }
        finally
        {
            IsSaving = false;
        }

        RequestClose?.Invoke(true);
    }

    public void Discard()
    {
        if (IsSaving || _saved || _discarded)
        {
            return;
        }

        _discarded = true;
        _applyPreview?.Invoke(_session.Current with
        {
            Theme = _entrySettings.Theme,
            WidgetOpacity = _entrySettings.WidgetOpacity,
            WidgetAlwaysOnTop = _entrySettings.WidgetAlwaysOnTop,
            DashboardWidth = _entrySettings.DashboardWidth,
            DashboardHeight = _entrySettings.DashboardHeight
        });
    }

    public void Cancel()
    {
        Discard();
        RequestClose?.Invoke(false);
    }
}
