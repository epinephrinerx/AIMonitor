using System.ComponentModel;
using System.Windows.Input;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// The compact floating widget (PAR-019, PAR-020), with the same rules as the Windows widget:
/// - it takes turns, every four seconds, through the services that returned data, never an
///   unconfigured one once it knows which those are;
/// - the chevrons show only when there is somewhere to go, and step at once without a fetch;
/// - choosing a service pins it; "All services (rotate)" lets them take turns again;
/// - the lead (five-hour) window supplies the severity and reset lines, the footer says how old the
///   reading is, and how many gauges fit is decided by the widget's width (<see cref="WidgetLayout"/>).
///
/// Holds no timer of its own: the window drives the rotation by calling <see cref="Advance"/> while
/// <see cref="IsRotating"/> is true, so every rule here can be tested without a dispatcher.
/// </summary>
public sealed class WidgetViewModel : ObservableObject
{
    /// <summary>The pace the Windows widget takes turns at.</summary>
    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(4);

    private readonly IReadOnlyList<ProviderViewModel> _providers;
    private int _rotateIndex;
    private string? _pinnedId;
    private bool _rotationEnabled = true;
    private double _opacity = 0.92;
    private bool _alwaysOnTop = true;

    private double _lastWidth = WidgetLayout.DefaultWidth;
    private double _lastHeight = WidgetLayout.DefaultHeight;
    private double _lastHeaderHeight = 16;
    private double _lastLineHeight = 14;
    private DateTimeOffset? _now;

    private IReadOnlyList<MeterViewModel> _visibleMeters = [];
    private double _arcSize = WidgetLayout.ArcMax;
    private double _cell = WidgetLayout.ArcMax;
    private bool _showSubtitle = true;
    private bool _inlineValue = true;
    private bool _isTooSmall;
    private string _statusText = string.Empty;
    private string _emptyMessage = string.Empty;
    private bool _showLeadHead;
    private string _leadHeadText = string.Empty;
    private Severity _leadSeverity = Severity.Normal;
    private bool _showResetLine;
    private string _resetLineText = string.Empty;
    private bool _showFooter;
    private bool _canPage;

    public WidgetViewModel(IReadOnlyList<ProviderViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException("The widget needs at least one service to show.", nameof(providers));
        }

        _providers = providers;

        NextCommand = new RelayCommand(() => Page(1));
        PreviousCommand = new RelayCommand(() => Page(-1));
        ExpandCommand = new RelayCommand(() => ExpandRequested?.Invoke());
        RefreshCommand = new RelayCommand(() => RefreshRequested?.Invoke());
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());

        foreach (var provider in _providers)
        {
            provider.PropertyChanged += OnProviderChanged;
        }

        RecalculateLayout();
    }

    public event Action? ExpandRequested;

    public event Action? RefreshRequested;

    public event Action? QuitRequested;

    /// <summary>Raised when a click on a chevron should restart the rotation interval.</summary>
    public event Action? RotationRestarted;

    /// <summary>Where the widget looks when rotation is off and nothing is pinned: the dashboard's tab.</summary>
    public Func<string?>? ActiveProviderSource { get; set; }

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand ExpandCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand QuitCommand { get; }

    public IReadOnlyList<ProviderViewModel> Providers => _providers;

    public ProviderViewModel Current
    {
        get
        {
            if (_pinnedId is not null && _providers.FirstOrDefault(p => p.ProviderId == _pinnedId) is { } pinned)
            {
                return pinned;
            }

            if (!_rotationEnabled)
            {
                var active = ActiveProviderSource?.Invoke();
                return _providers.FirstOrDefault(p => p.ProviderId == active) ?? _providers[0];
            }

            var showable = Showable();
            return showable[_rotateIndex % showable.Count];
        }
    }

    public string Title => Current.Title;

    /// <summary>True when the chevrons would take you somewhere.</summary>
    public bool CanPage
    {
        get => _canPage;
        private set => Set(ref _canPage, value);
    }

    public string? PinnedProviderId => _pinnedId;

    public bool IsPinned
    {
        get => _pinnedId is not null;
        set
        {
            if (value == IsPinned) return;
            _pinnedId = value ? Current.ProviderId : null;
            AfterSelectionChanged();
        }
    }

    /// <summary>The settings flag: take turns through the services (unless one is pinned).</summary>
    public bool RotationEnabled
    {
        get => _rotationEnabled;
        private set
        {
            if (Set(ref _rotationEnabled, value)) AfterSelectionChanged();
        }
    }

    /// <summary>"All services (rotate)" is the checked item in the Show menu.</summary>
    public bool IsRotatingAll => _rotationEnabled && _pinnedId is null;

    /// <summary>Whether the window's timer should be running: rotating, unpinned, and somewhere to go.</summary>
    public bool IsRotating => IsRotatingAll && Showable().Count > 1;

    public double Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, Math.Clamp(value, SettingsViewModel.MinimumOpacity, 1.0));
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => Set(ref _alwaysOnTop, value);
    }

    public IReadOnlyList<MeterViewModel> VisibleMeters
    {
        get => _visibleMeters;
        private set
        {
            if (Set(ref _visibleMeters, value)) Raise(nameof(HasMeters));
        }
    }

    public bool HasMeters => _visibleMeters.Count > 0 && !_isTooSmall;

    /// <summary>The gauge's drawn size; the cell is the width each gauge is given.</summary>
    public double ArcSize
    {
        get => _arcSize;
        private set => Set(ref _arcSize, value);
    }

    public double Cell
    {
        get => _cell;
        private set => Set(ref _cell, value);
    }

    public bool ShowSubtitle
    {
        get => _showSubtitle;
        private set => Set(ref _showSubtitle, value);
    }

    public bool InlineValue
    {
        get => _inlineValue;
        private set => Set(ref _inlineValue, value);
    }

    /// <summary>Too small to draw even one gauge: the widget says so instead.</summary>
    public bool IsTooSmall
    {
        get => _isTooSmall;
        private set
        {
            if (Set(ref _isTooSmall, value)) Raise(nameof(HasMeters));
        }
    }

    /// <summary>"updated just now" / "updated 5m ago"; empty until the service has been read once.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool ShowFooter
    {
        get => _showFooter;
        private set => Set(ref _showFooter, value);
    }

    /// <summary>"Waiting for data…", the service's error, or "X is not configured yet." when no gauge can be drawn.</summary>
    public string EmptyMessage
    {
        get => _emptyMessage;
        private set
        {
            if (Set(ref _emptyMessage, value)) Raise(nameof(HasEmptyMessage));
        }
    }

    public bool HasEmptyMessage => _emptyMessage.Length > 0 && !_isTooSmall;

    /// <summary>Lead window name, with glyph and word once it is no longer normal ("⚠ Session High").</summary>
    public string LeadHeadText
    {
        get => _leadHeadText;
        private set => Set(ref _leadHeadText, value);
    }

    public Severity LeadSeverity
    {
        get => _leadSeverity;
        private set
        {
            if (!Set(ref _leadSeverity, value)) return;
            Raise(nameof(IsLeadNormal));
            Raise(nameof(IsLeadHigh));
            Raise(nameof(IsLeadVeryHigh));
            Raise(nameof(IsLeadCritical));
        }
    }

    // One boolean per severity, because Avalonia binds each style class on its own.
    public bool IsLeadNormal => _leadSeverity == Severity.Normal;

    public bool IsLeadHigh => _leadSeverity == Severity.High;

    public bool IsLeadVeryHigh => _leadSeverity == Severity.VeryHigh;

    public bool IsLeadCritical => _leadSeverity == Severity.Critical;

    public bool ShowLeadHead
    {
        get => _showLeadHead;
        private set => Set(ref _showLeadHead, value);
    }

    public string ResetLineText
    {
        get => _resetLineText;
        private set => Set(ref _resetLineText, value);
    }

    public bool ShowResetLine
    {
        get => _showResetLine;
        private set => Set(ref _showResetLine, value);
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Opacity = settings.WidgetOpacity;
        AlwaysOnTop = settings.WidgetAlwaysOnTop;
        RotationEnabled = settings.WidgetRotationEnabled;
    }

    /// <summary>The widget's size and font metrics, which decide how many gauges fit.</summary>
    public void UpdateLayout(double width, double height, double headerHeight, double lineHeight)
    {
        _lastWidth = width;
        _lastHeight = height;
        _lastHeaderHeight = headerHeight;
        _lastLineHeight = lineHeight;
        RecalculateLayout();
    }

    /// <summary>Re-reads the age of the reading; called about once a second while the widget is up.</summary>
    public void RefreshStatus(DateTimeOffset? now = null)
    {
        _now = now;
        RecalculateLayout();
    }

    /// <summary>One step of the rotation. Does nothing while pinned, with rotation off, or with one service.</summary>
    public void Advance()
    {
        if (!IsRotating) return;
        var showable = Showable();
        _rotateIndex = (_rotateIndex + 1) % showable.Count;
        AfterSelectionChanged();
    }

    /// <summary>A Show-menu choice: pins the service, so turning away does not undo the click a moment later.</summary>
    public void ShowProvider(string providerId)
    {
        _pinnedId = providerId;
        _rotationEnabled = false;
        Raise(nameof(RotationEnabled));
        AfterSelectionChanged();
    }

    /// <summary>"All services (rotate)": unpin and take turns again.</summary>
    public void ShowAllProviders()
    {
        _pinnedId = null;
        _rotationEnabled = true;
        Raise(nameof(RotationEnabled));
        AfterSelectionChanged();
    }

    /// <summary>The dashboard's tab changed; with rotation off that is the service the widget shows.</summary>
    public void OnActiveProviderChanged()
    {
        if (!_rotationEnabled && _pinnedId is null) AfterSelectionChanged();
    }

    internal static string FormatStatus(DateTimeOffset? lastUpdated, DateTimeOffset now)
    {
        if (lastUpdated is not DateTimeOffset last) return string.Empty;

        var age = now - last;
        return age.TotalSeconds < 10 ? "updated just now" : $"updated {ResetDisplayFormatter.FormatCountdown(age)} ago";
    }

    /// <summary>"Services worth taking turns through: the ones that returned data" (1.3.3 <c>_rotatable_ids</c>).</summary>
    private List<ProviderViewModel> Showable()
    {
        if (!_providers.Any(p => p.HasSnapshot)) return [.. _providers];

        var configured = _providers.Where(p => p.Configured).ToList();
        return configured.Count > 0 ? configured : [.. _providers];
    }

    /// <summary>
    /// Steps to the next showable service now, without a fetch. With rotation off or a service
    /// pinned, the only way to say "show that one" is to pin it; otherwise the turn moves on and its
    /// interval starts again.
    /// </summary>
    private void Page(int step)
    {
        var showable = Showable();
        if (showable.Count < 2) return;

        var here = Math.Max(0, showable.IndexOf(Current));
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
        RotationRestarted?.Invoke();
    }

    private void OnProviderChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProviderViewModel.Configured):
            case nameof(ProviderViewModel.HasSnapshot):
                // The set of showable services changed; the chevrons and the timer depend on it.
                AfterSelectionChanged();
                break;
            case nameof(ProviderViewModel.Meters):
            case nameof(ProviderViewModel.FetchedAt):
            case nameof(ProviderViewModel.ErrorMessage):
                if (ReferenceEquals(sender, Current)) RecalculateLayout();
                break;
        }
    }

    private void AfterSelectionChanged()
    {
        Raise(nameof(Current));
        Raise(nameof(Title));
        Raise(nameof(IsPinned));
        Raise(nameof(PinnedProviderId));
        Raise(nameof(IsRotatingAll));
        Raise(nameof(IsRotating));
        RecalculateLayout();
    }

    private void RecalculateLayout()
    {
        var current = Current;
        var withValues = current.Meters.Where(HasValue).ToList();

        StatusText = FormatStatus(current.FetchedAt, _now ?? DateTimeOffset.UtcNow);
        var hasStatus = StatusText.Length > 0;

        var layout = WidgetLayout.Compute(_lastWidth, _lastHeight, withValues.Count, _lastHeaderHeight, _lastLineHeight, hasStatus);

        var shown = withValues.Take(layout.Count).ToList();
        if (!VisibleMeters.SequenceEqual(shown, ReferenceEqualityComparer.Instance))
        {
            VisibleMeters = shown;
        }

        Cell = layout.Cell;
        ArcSize = layout.Arc;
        ShowSubtitle = layout.CaptionLines == 2;
        InlineValue = layout.InlineValue;
        IsTooSmall = layout.TooSmall;

        EmptyMessage = withValues.Count > 0 ? string.Empty : FormatEmptyMessage(current);
        Raise(nameof(HasEmptyMessage));
        CanPage = Showable().Count > 1;
        Raise(nameof(IsRotatingAll));
        Raise(nameof(IsRotating));
        PlaceCaptionLines(layout, shown, hasStatus);
    }

    /// <summary>
    /// Decides which of the lines under the gauges still fit. Each is dropped rather than overprinted:
    /// the head line, then the reset line, then the footer, in that order of importance.
    /// </summary>
    private void PlaceCaptionLines(WidgetLayoutResult layout, IReadOnlyList<MeterViewModel> shown, bool hasStatus)
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
        if (ShowLeadHead) cursor += line;

        ResetLineText = lead.ResetCompact;
        ShowResetLine = ResetLineText.Length > 0 && cursor + line <= footerTop;
        if (ShowResetLine) cursor += line;

        ShowFooter = hasStatus && cursor <= footerTop;
    }

    private static string FormatEmptyMessage(ProviderViewModel provider)
    {
        if (provider.HasError) return provider.ErrorMessage;

        return provider.HasSnapshot && !provider.Configured
            ? $"{provider.Title} is not configured yet."
            : "Waiting for data…";
    }

    /// <summary>A gauge is drawn only for a reading with a value; "--" windows are left to the dashboard.</summary>
    private static bool HasValue(MeterViewModel meter) =>
        !string.IsNullOrWhiteSpace(meter.ValueText) && meter.ValueText != "--";
}
