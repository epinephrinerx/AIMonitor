using System.Windows.Input;
using System.Windows.Threading;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf;
using Brush = System.Windows.Media.Brush;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// ViewModel for the compact floating widget window (PAR-019, PAR-020), following 1.3.3 <c>CompactView</c>
/// and the widget half of <c>main_window.py</c>:
/// - the widget cycles every four seconds through the services that returned data, never an unconfigured one
/// - chevrons appear only when there is somewhere to go, and step immediately without a network fetch
/// - choosing a service pins it; "All services" restores rotation; a click restarts the interval
/// - the lead (five-hour) window supplies the severity and reset lines; the footer says how old the reading is
/// </summary>
public sealed class WidgetViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ProviderTabViewModel> _providers;
    private readonly DispatcherTimer _rotationTimer;
    private int _rotateIndex;
    private string? _pinnedId;
    private bool _rotationEnabled = true;
    private bool _isActive;
    private double _opacity = 0.92;
    private bool _alwaysOnTop = true;

    private double _lastWidth = WidgetLayout.DefaultWidth;
    private double _lastHeight = WidgetLayout.DefaultHeight;
    private double _lastHeaderHeight;
    private double _lastLineHeight;

    private IReadOnlyList<MeterDisplayItem> _visibleMeters = [];
    private double _arcSize;
    private double _cell;
    private int _captionLines = 2;
    private double _lineHeight;
    private double _headerHeight;
    private double _headerRowHeight;
    private double _footerRowHeight;
    private bool _showSubtitle = true;
    private bool _inlineValue = true;
    private bool _isTooSmall;
    private string _statusText = "";
    private string _emptyMessage = "";
    private bool _showLeadHead;
    private string _leadHeadText = "";
    private Severity _leadSeverity = Severity.Normal;
    private bool _showResetLine;
    private string _resetLineText = "";
    private bool _showFooter;
    private bool _canPage;

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
                switch (e.PropertyName)
                {
                    case nameof(ProviderTabViewModel.IsConfigured):
                    case nameof(ProviderTabViewModel.HasSnapshot):
                        // The set of showable services changed; the chevrons and the timer depend on it.
                        OnPropertyChanged(nameof(CurrentProvider));
                        SyncRotation();
                        RecalculateLayout();
                        break;
                    case nameof(ProviderTabViewModel.Meters):
                    case nameof(ProviderTabViewModel.Status):
                    case nameof(ProviderTabViewModel.LastUpdated):
                    case nameof(ProviderTabViewModel.ErrorMessage):
                        if (ReferenceEquals(s, CurrentProvider))
                        {
                            RecalculateLayout();
                        }

                        break;
                }
            };
        }

        _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _rotationTimer.Tick += (s, e) => AdvanceRotation();

        RecalculateLayout();
    }

    /// <summary>Where the widget looks when rotation is off and nothing is pinned: the dashboard's tab.</summary>
    public Func<string?>? ActiveProviderSource { get; set; }

    public event Action? RequestExpand;

    public event Action? RequestRefresh;

    public event Action? RequestQuit;

    public ProviderTabViewModel? CurrentProvider
    {
        get
        {
            if (_providers.Count == 0)
            {
                return null;
            }

            if (_pinnedId is not null && _providers.FirstOrDefault(p => p.ProviderId == _pinnedId) is { } pinned)
            {
                return pinned;
            }

            var showable = Showable();
            if (!_rotationEnabled)
            {
                var active = ActiveProviderSource?.Invoke();
                return _providers.FirstOrDefault(p => p.ProviderId == active) ?? _providers[0];
            }

            return showable[_rotateIndex % showable.Count];
        }
    }

    /// <summary>"Services worth cycling through: the ones that returned data" (1.3.3 <c>_rotatable_ids</c>).</summary>
    private IReadOnlyList<ProviderTabViewModel> Showable()
    {
        if (!_providers.Any(p => p.HasSnapshot))
        {
            return _providers;
        }

        var configured = _providers.Where(p => p.IsConfigured).ToList();
        return configured.Count > 0 ? configured : _providers;
    }

    /// <summary>True when the chevrons would take you somewhere.</summary>
    public bool CanPage
    {
        get => _canPage;
        private set => SetProperty(ref _canPage, value);
    }

    public string? PinnedProviderId => _pinnedId;

    public bool IsPinned
    {
        get => _pinnedId is not null;
        set
        {
            if (value == IsPinned)
            {
                return;
            }

            _pinnedId = value ? CurrentProvider?.ProviderId : null;
            AfterSelectionChanged();
        }
    }

    /// <summary>Settings flag: cycle through the services (unless one is pinned).</summary>
    public bool RotationEnabled
    {
        get => _rotationEnabled;
        private set
        {
            if (SetProperty(ref _rotationEnabled, value))
            {
                AfterSelectionChanged();
            }
        }
    }

    /// <summary>"All services (rotate)" is the checked item in the Show menu.</summary>
    public bool IsRotatingAll => _rotationEnabled && _pinnedId is null;

    public IReadOnlyList<ProviderTabViewModel> Providers => _providers;

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
        private set
        {
            if (SetProperty(ref _visibleMeters, value))
            {
                OnPropertyChanged(nameof(HasMeters));
            }
        }
    }

    public bool HasMeters => _visibleMeters.Count > 0;

    public double ArcSize
    {
        get => _arcSize;
        private set => SetProperty(ref _arcSize, value);
    }

    public double Cell
    {
        get => _cell;
        private set => SetProperty(ref _cell, value);
    }

    public int CaptionLines
    {
        get => _captionLines;
        private set => SetProperty(ref _captionLines, value);
    }

    public double LineHeight
    {
        get => _lineHeight;
        private set => SetProperty(ref _lineHeight, value);
    }

    public double HeaderHeight
    {
        get => _headerHeight;
        private set => SetProperty(ref _headerHeight, value);
    }

    public double HeaderRowHeight
    {
        get => _headerRowHeight;
        private set => SetProperty(ref _headerRowHeight, value);
    }

    public double FooterRowHeight
    {
        get => _footerRowHeight;
        private set => SetProperty(ref _footerRowHeight, value);
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

    /// <summary>"updated just now" / "updated 5m ago"; empty until the service has been read once.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool ShowFooter
    {
        get => _showFooter;
        private set => SetProperty(ref _showFooter, value);
    }

    /// <summary>"Waiting for data…", the service's error, or "X is not configured yet." when no gauge can be drawn.</summary>
    public string EmptyMessage
    {
        get => _emptyMessage;
        private set => SetProperty(ref _emptyMessage, value);
    }

    /// <summary>Lead window name, with glyph and word once it is no longer normal ("⚠ Session High").</summary>
    public string LeadHeadText
    {
        get => _leadHeadText;
        private set => SetProperty(ref _leadHeadText, value);
    }

    public Severity LeadSeverity
    {
        get => _leadSeverity;
        private set
        {
            if (SetProperty(ref _leadSeverity, value))
            {
                OnPropertyChanged(nameof(LeadHeadBrush));
            }
        }
    }

    /// <summary>Ink for a normal lead window, the traffic-light colour otherwise.</summary>
    public Brush LeadHeadBrush => _leadSeverity == Severity.Normal
        ? (System.Windows.Application.Current?.Resources["TextSecondaryBrush"] as Brush ?? System.Windows.Media.Brushes.Gray)
        : AIMonitor.Presentation.Wpf.Theme.ThemeManager.SeverityBrush(_leadSeverity);

    public bool ShowLeadHead
    {
        get => _showLeadHead;
        private set => SetProperty(ref _showLeadHead, value);
    }

    public string ResetLineText
    {
        get => _resetLineText;
        private set => SetProperty(ref _resetLineText, value);
    }

    public bool ShowResetLine
    {
        get => _showResetLine;
        private set => SetProperty(ref _showResetLine, value);
    }

    /// <summary>
    /// Gets the number of times <see cref="ApplySettings"/> has been called.
    /// Used for verification that settings are forwarded on every apply.
    /// </summary>
    public int ApplySettingsCallCount { get; private set; }

    /// <summary>
    /// Applies updated opacity, always-on-top and rotation settings to the widget.
    /// Property-changed notifications are only raised when property values actually change.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplySettingsCallCount++;
        Opacity = settings.WidgetOpacity;
        AlwaysOnTop = settings.WidgetAlwaysOnTop;
        RotationEnabled = settings.WidgetRotationEnabled;
    }

    /// <summary>The widget window is on screen: run the rotation timer only while it is.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        SyncRotation();
        if (active)
        {
            RecalculateLayout();
        }
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

    /// <summary>Re-reads the age of the reading; called about once a second while the widget is up.</summary>
    public void RefreshStatus(DateTimeOffset? now = null) => RecalculateLayout(now);

    private void RecalculateLayout(DateTimeOffset? now = null)
    {
        var currentProvider = CurrentProvider;
        var validMeters = currentProvider?.Meters
            .Where(HasValue)
            .ToList() ?? [];

        StatusText = FormatStatus(currentProvider?.LastUpdated, now ?? DateTimeOffset.UtcNow);
        var hasStatus = !string.IsNullOrEmpty(StatusText);

        var layout = WidgetLayout.Compute(
            _lastWidth,
            _lastHeight,
            validMeters.Count,
            _lastHeaderHeight,
            _lastLineHeight,
            hasStatus);

        var newVisibleMeters = validMeters.Take(layout.Count).ToList();
        if (!VisibleMeters.SequenceEqual(newVisibleMeters, ReferenceEqualityComparer.Instance))
        {
            VisibleMeters = newVisibleMeters;
        }

        Cell = layout.Cell;
        CaptionLines = layout.CaptionLines;
        ArcSize = layout.Arc;
        ShowSubtitle = layout.CaptionLines == 2;
        InlineValue = layout.InlineValue;
        IsTooSmall = layout.TooSmall;

        LineHeight = _lastLineHeight;
        HeaderHeight = _lastHeaderHeight;
        HeaderRowHeight = _lastHeaderHeight + 5.0;
        FooterRowHeight = hasStatus ? _lastLineHeight + 4.0 : 0.0;

        EmptyMessage = validMeters.Count > 0 ? "" : FormatEmptyMessage(currentProvider);
        CanPage = Showable().Count > 1;
        OnPropertyChanged(nameof(IsRotatingAll));
        PlaceCaptionLines(layout, newVisibleMeters, hasStatus);
    }

    /// <summary>
    /// Decides which of the lines under the gauges still fit. Each is dropped rather than overprinted:
    /// head line, then reset line, then the footer, in that order of importance (1.3.3 <c>paintEvent</c>).
    /// </summary>
    private void PlaceCaptionLines(WidgetLayoutResult layout, IReadOnlyList<MeterDisplayItem> shown, bool hasStatus)
    {
        if (shown.Count == 0 || layout.TooSmall)
        {
            ShowLeadHead = false;
            ShowResetLine = false;
            ShowFooter = hasStatus && !layout.TooSmall && shown.Count == 0;
            return;
        }

        var line = _lastLineHeight;
        var y = WidgetLayout.Margin + _lastHeaderHeight + 5.0;
        var labelBlock = line * layout.CaptionLines + 3.0;
        var cursor = y + layout.Arc + 2.0 + labelBlock + 4.0;
        var footerTop = _lastHeight - WidgetLayout.Margin - (hasStatus ? line : 0.0);

        var lead = shown.FirstOrDefault(m => m.IsFiveHour) ?? shown[0];
        LeadSeverity = lead.Severity;
        var (glyph, word) = SeverityLabels.Describe(lead.Severity);
        LeadHeadText = lead.Severity == Severity.Normal ? lead.Title : $"{glyph} {lead.Title} {word}";
        ShowLeadHead = cursor + line <= footerTop;
        if (ShowLeadHead)
        {
            cursor += line;
        }

        ResetLineText = lead.ResetCompact;
        ShowResetLine = !string.IsNullOrEmpty(ResetLineText) && cursor + line <= footerTop;
        if (ShowResetLine)
        {
            cursor += line;
        }

        ShowFooter = hasStatus && cursor <= footerTop;
    }

    internal static string FormatStatus(DateTimeOffset? lastUpdated, DateTimeOffset now)
    {
        if (lastUpdated is not DateTimeOffset last)
        {
            return "";
        }

        var age = now - last;
        return age.TotalSeconds < 10 ? "updated just now" : $"updated {ResetDisplayFormatter.FormatCountdown(age)} ago";
    }

    private string FormatEmptyMessage(ProviderTabViewModel? provider)
    {
        if (provider is null)
        {
            return "Waiting for data…";
        }

        if (!string.IsNullOrEmpty(provider.ErrorMessage))
        {
            return provider.ErrorMessage;
        }

        return provider.HasSnapshot && !provider.IsConfigured
            ? $"{provider.DisplayName} is not configured yet."
            : "Waiting for data…";
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

    public void NextProvider() => Page(1);

    public void PreviousProvider() => Page(-1);

    /// <summary>
    /// Steps to the next showable service now, without a fetch. With rotation off or a service pinned, the
    /// only way to say "show that one" is to pin it; otherwise the rotation index moves and its interval restarts.
    /// </summary>
    private void Page(int step)
    {
        var showable = Showable();
        if (showable.Count < 2)
        {
            return;
        }

        var here = Math.Max(0, ListIndex(showable, CurrentProvider));
        var target = (here + step + showable.Count) % showable.Count;

        if (_pinnedId is not null || !_rotationEnabled)
        {
            _pinnedId = showable[target].ProviderId;
        }
        else
        {
            _rotateIndex = target;
        }

        AfterSelectionChanged();
        if (_rotationTimer.IsEnabled)
        {
            _rotationTimer.Stop();
            _rotationTimer.Start();
        }
    }

    /// <summary>A menu choice: pins the service so rotating away does not undo the click a second later.</summary>
    public void ShowProvider(string providerId)
    {
        _pinnedId = providerId;
        _rotationEnabled = false;
        AfterSelectionChanged();
        OnPropertyChanged(nameof(RotationEnabled));
    }

    /// <summary>"All services (rotate)": unpin and start cycling again.</summary>
    public void ShowAllProviders()
    {
        _pinnedId = null;
        _rotationEnabled = true;
        AfterSelectionChanged();
        OnPropertyChanged(nameof(RotationEnabled));
    }

    public void Expand() => RequestExpand?.Invoke();

    public void Refresh() => RequestRefresh?.Invoke();

    public void Quit() => RequestQuit?.Invoke();

    private void AdvanceRotation()
    {
        var showable = Showable();
        if (showable.Count < 2)
        {
            return;
        }

        _rotateIndex = (_rotateIndex + 1) % showable.Count;
        AfterSelectionChanged();
    }

    private void AfterSelectionChanged()
    {
        OnPropertyChanged(nameof(CurrentProvider));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinnedProviderId));
        OnPropertyChanged(nameof(IsRotatingAll));
        SyncRotation();
        RecalculateLayout();
    }

    /// <summary>Run the timer only while the widget is up, rotating, unpinned and has somewhere to go.</summary>
    private void SyncRotation()
    {
        var rotate = _isActive && _rotationEnabled && _pinnedId is null && Showable().Count > 1;
        if (rotate && !_rotationTimer.IsEnabled)
        {
            _rotationTimer.Start();
        }
        else if (!rotate && _rotationTimer.IsEnabled)
        {
            _rotationTimer.Stop();
        }
    }

    internal bool IsRotationTimerRunning => _rotationTimer.IsEnabled;

    private static int ListIndex(IReadOnlyList<ProviderTabViewModel> list, ProviderTabViewModel? item)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
