using System.Windows.Input;
using System.Windows.Threading;
using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf;

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

    private double _lastWidth = WidgetLayout.DefaultWidth;
    private double _lastHeight = WidgetLayout.DefaultHeight;
    private double _lastHeaderHeight;
    private double _lastLineHeight;

    private IReadOnlyList<MeterDisplayItem> _visibleMeters = [];
    private double _arcSize;
    private bool _showSubtitle = true;
    private bool _inlineValue = true;
    private bool _isTooSmall;

    public WidgetViewModel(IReadOnlyList<ProviderTabViewModel> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));

        NextProviderCommand = new RelayCommand(NextProvider);
        PreviousProviderCommand = new RelayCommand(PreviousProvider);
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);

        foreach (var provider in _providers)
        {
            provider.PropertyChanged += (s, e) =>
            {
                if (ReferenceEquals(s, CurrentProvider) &&
                    (e.PropertyName == nameof(ProviderTabViewModel.Meters) ||
                     e.PropertyName == nameof(ProviderTabViewModel.Status)))
                {
                    RecalculateLayout();
                }
            };
        }

        RecalculateLayout();

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

    public IReadOnlyList<MeterDisplayItem> VisibleMeters
    {
        get => _visibleMeters;
        private set => SetProperty(ref _visibleMeters, value);
    }

    public double ArcSize
    {
        get => _arcSize;
        private set => SetProperty(ref _arcSize, value);
    }

    public bool ShowSubtitle
    {
        get => _showSubtitle;
        private set => SetProperty(ref _showSubtitle, value);
    }

    public bool InlineValue
    {
        get => _inlineValue;
        private set => SetProperty(ref _inlineValue, value);
    }

    public bool IsTooSmall
    {
        get => _isTooSmall;
        private set => SetProperty(ref _isTooSmall, value);
    }

    /// <summary>
    /// Gets the number of times <see cref="ApplySettings"/> has been called.
    /// Used for verification that settings are forwarded on every apply.
    /// </summary>
    public int ApplySettingsCallCount { get; private set; }

    /// <summary>
    /// Applies updated opacity and always-on-top settings to the widget.
    /// Property-changed notifications are only raised when property values actually change.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplySettingsCallCount++;
        Opacity = settings.WidgetOpacity;
        AlwaysOnTop = settings.WidgetAlwaysOnTop;
    }

    /// <summary>
    /// Updates the widget layout dimensions and recalculates visible meters and gauge sizing.
    /// </summary>
    public void UpdateLayout(double width, double height, double headerHeight, double lineHeight)
    {
        _lastWidth = width;
        _lastHeight = height;
        _lastHeaderHeight = headerHeight;
        _lastLineHeight = lineHeight;
        RecalculateLayout();
    }

    private void RecalculateLayout()
    {
        var currentProvider = CurrentProvider;
        var validMeters = currentProvider?.Meters
            .Where(HasValue)
            .ToList() ?? [];

        bool hasStatus = !string.IsNullOrEmpty(currentProvider?.Status);

        var layout = WidgetLayout.Compute(
            _lastWidth,
            _lastHeight,
            validMeters.Count,
            _lastHeaderHeight,
            _lastLineHeight,
            hasStatus);

        VisibleMeters = validMeters.Take(layout.Count).ToList();
        ArcSize = layout.Arc;
        ShowSubtitle = layout.CaptionLines == 2;
        InlineValue = layout.InlineValue;
        IsTooSmall = layout.TooSmall;
    }

    /// <summary>
    /// Determines whether a meter has a value according to the existing convention in ProviderTabViewModel.cs:
    /// meter is present, ValueText is not empty/whitespace, and ValueText is not the unvalued placeholder "--".
    /// </summary>
    private static bool HasValue(MeterDisplayItem? meter) =>
        meter is not null && !string.IsNullOrWhiteSpace(meter.ValueText) && meter.ValueText != "--";

    public ICommand NextProviderCommand { get; }
    public ICommand PreviousProviderCommand { get; }
    public ICommand TogglePinCommand { get; }

    public void NextProvider()
    {
        if (_providers.Count <= 1) return;
        _currentIndex = (_currentIndex + 1) % _providers.Count;
        OnPropertyChanged(nameof(CurrentProvider));
        RecalculateLayout();
    }

    public void PreviousProvider()
    {
        if (_providers.Count <= 1) return;
        _currentIndex = (_currentIndex - 1 + _providers.Count) % _providers.Count;
        OnPropertyChanged(nameof(CurrentProvider));
        RecalculateLayout();
    }
}
