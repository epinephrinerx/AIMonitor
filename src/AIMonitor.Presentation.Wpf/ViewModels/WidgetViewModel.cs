using System.Windows.Input;
using System.Windows.Threading;
using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// ViewModel for the compact floating widget window.
/// Satisfies PAR-019 and PAR-020:
/// - Compact mode with opacity and always-on-top
/// - 4-second carousel rotation of active providers
/// - Immediate chevron navigation without network fetch
/// - Pin support to pause automatic rotation
/// </summary>
public sealed class WidgetViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ProviderTabViewModel> _providers;
    private readonly DispatcherTimer _rotationTimer;
    private int _currentIndex;
    private bool _isPinned;
    private double _opacity = 0.92;
    private bool _alwaysOnTop = true;

    public WidgetViewModel(IReadOnlyList<ProviderTabViewModel> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));

        NextProviderCommand = new RelayCommand(NextProvider);
        PreviousProviderCommand = new RelayCommand(PreviousProvider);
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);

        // 4-second rotation timer (PAR-020)
        _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _rotationTimer.Tick += (s, e) =>
        {
            if (!_isPinned) NextProvider();
        };
        _rotationTimer.Start();
    }

    public ProviderTabViewModel? CurrentProvider =>
        _providers.Count > 0 ? _providers[_currentIndex % _providers.Count] : null;

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetProperty(ref _alwaysOnTop, value);
    }

    /// <summary>
    /// Applies updated opacity and always-on-top settings to the widget.
    /// Property-changed notifications are only raised when property values actually change.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Opacity = settings.WidgetOpacity;
        AlwaysOnTop = settings.WidgetAlwaysOnTop;
    }

    public ICommand NextProviderCommand { get; }
    public ICommand PreviousProviderCommand { get; }
    public ICommand TogglePinCommand { get; }

    public void NextProvider()
    {
        if (_providers.Count <= 1) return;
        _currentIndex = (_currentIndex + 1) % _providers.Count;
        OnPropertyChanged(nameof(CurrentProvider));
    }

    public void PreviousProvider()
    {
        if (_providers.Count <= 1) return;
        _currentIndex = (_currentIndex - 1 + _providers.Count) % _providers.Count;
        OnPropertyChanged(nameof(CurrentProvider));
    }
}
