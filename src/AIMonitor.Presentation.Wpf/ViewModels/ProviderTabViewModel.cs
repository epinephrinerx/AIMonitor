using System.Windows.Media;
using AIMonitor.Domain;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class MeterDisplayItem : ViewModelBase
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public double Value { get; init; }
    public string ValueText { get; init; } = "0%";
    public Severity Severity { get; init; } = Severity.Normal;
    public Brush GaugeBrush { get; init; } = Brushes.Teal;

    /// <summary>Glyph plus word ("✓ Normal", "⚠ High") so colour never carries severity alone.</summary>
    public string SeverityText { get; init; } = "";

    /// <summary>"Resets in 2h 5m · 17:50", "No reset scheduled", or the server's lock reason.</summary>
    public string ResetText { get; init; } = "";

    /// <summary>The five-hour (session) window, which the widget caption and tray speak for.</summary>
    public bool IsFiveHour { get; init; }

    /// <summary>Widget wording: "resets in 2h 5m  ·  17:50", "no reset scheduled", "resetting now".</summary>
    public string ResetCompact { get; init; } = "";
}

/// <summary>A plain numeric readout shown in the stats row under the gauges.</summary>
public sealed record StatTile(string Label, string Value, string Detail);

public sealed class ProviderTabViewModel : ViewModelBase
{
    private string _status = "Checking...";
    private string _statusDetail = "";
    private Severity _severity = Severity.Normal;
    private IReadOnlyList<MeterDisplayItem> _meters = [];
    private IReadOnlyList<UsageHistoryBucket> _chartBuckets = [];
    private IReadOnlyList<string> _chartSeries = [];
    private IReadOnlyList<UsageHistoryBreakdown> _modelRows = [];
    private IReadOnlyList<UsageHistoryBreakdown> _projectRows = [];
    private string _chartMetric = "Total tokens";
    private string _chartTitle = "Usage per day";
    private string _modelTitle = "By model";
    private string _projectTitle = "By project";
    private string _historyNote = "";
    private string _valueNote = "";
    private bool _hasHistory;
    private string _statsSummary = "";
    private string _accountInfo = "";
    private string _errorMessage = "";
    private IReadOnlyList<StatTile> _statTiles = [];
    private string _tabTitle;
    private string _accountLabel = "";
    private bool _showSetupCard;
    private bool _isConfigured;
    private bool _hasSnapshot;
    private DateTimeOffset? _lastUpdated;

    public ProviderTabViewModel(string providerId, string displayName)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        _tabTitle = displayName;
    }

    /// <summary>Tab caption: the name plus the lead meter's percentage, e.g. "Claude 12%".</summary>
    public string TabTitle
    {
        get => _tabTitle;
        private set => SetProperty(ref _tabTitle, value);
    }

    /// <summary>Header line under the app title: the account, or why there is none.</summary>
    public string AccountLabel
    {
        get => _accountLabel;
        private set => SetProperty(ref _accountLabel, value);
    }

    public IReadOnlyList<StatTile> StatTiles
    {
        get => _statTiles;
        private set => SetProperty(ref _statTiles, value);
    }

    /// <summary>True when the service is not set up and has nothing to show yet.</summary>
    public bool ShowSetupCard
    {
        get => _showSetupCard;
        private set => SetProperty(ref _showSetupCard, value);
    }

    /// <summary>True once the service is set up (connected, limited or expired).</summary>
    public bool IsConfigured
    {
        get => _isConfigured;
        private set => SetProperty(ref _isConfigured, value);
    }

    /// <summary>True once any snapshot has been received; before that every service counts as showable.</summary>
    public bool HasSnapshot
    {
        get => _hasSnapshot;
        private set => SetProperty(ref _hasSnapshot, value);
    }

    /// <summary>When the last reading was taken, for the widget's "updated … ago" line.</summary>
    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        set => SetProperty(ref _lastUpdated, value);
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public string SetupTitle => $"{DisplayName} is not set up yet";

    public string SetupHint => ProviderMeta.TryGet(ProviderId)?.SetupHint ?? "";

    public string ProviderId { get; }
    public string DisplayName { get; }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set => SetProperty(ref _statusDetail, value);
    }

    public Severity Severity
    {
        get => _severity;
        set => SetProperty(ref _severity, value);
    }

    public IReadOnlyList<MeterDisplayItem> Meters
    {
        get => _meters;
        set => SetProperty(ref _meters, value);
    }

    /// <summary>Day buckets for the stacked chart (oldest first).</summary>
    public IReadOnlyList<UsageHistoryBucket> ChartBuckets
    {
        get => _chartBuckets;
        private set => SetProperty(ref _chartBuckets, value);
    }

    public IReadOnlyList<string> ChartSeries
    {
        get => _chartSeries;
        private set => SetProperty(ref _chartSeries, value);
    }

    public string ChartMetric
    {
        get => _chartMetric;
        private set => SetProperty(ref _chartMetric, value);
    }

    /// <summary>"Usage per day · last 14 days".</summary>
    public string ChartTitle
    {
        get => _chartTitle;
        private set => SetProperty(ref _chartTitle, value);
    }

    public IReadOnlyList<UsageHistoryBreakdown> ModelRows
    {
        get => _modelRows;
        private set
        {
            if (SetProperty(ref _modelRows, value))
            {
                OnPropertyChanged(nameof(HasModelRows));
            }
        }
    }

    public IReadOnlyList<UsageHistoryBreakdown> ProjectRows
    {
        get => _projectRows;
        private set
        {
            if (SetProperty(ref _projectRows, value))
            {
                OnPropertyChanged(nameof(HasProjectRows));
            }
        }
    }

    public string ModelTitle
    {
        get => _modelTitle;
        private set => SetProperty(ref _modelTitle, value);
    }

    public string ProjectTitle
    {
        get => _projectTitle;
        private set => SetProperty(ref _projectTitle, value);
    }

    /// <summary>True when there is a history to chart; hides the three chart cards otherwise.</summary>
    public bool HasHistory
    {
        get => _hasHistory;
        private set => SetProperty(ref _hasHistory, value);
    }

    public bool HasModelRows => _modelRows.Count > 0;

    public bool HasProjectRows => _projectRows.Count > 0;

    /// <summary>A history-only problem, shown above the charts rather than in the top error banner.</summary>
    public string HistoryNote
    {
        get => _historyNote;
        private set
        {
            if (SetProperty(ref _historyNote, value))
            {
                OnPropertyChanged(nameof(HasHistoryNote));
            }
        }
    }

    public bool HasHistoryNote => !string.IsNullOrEmpty(_historyNote);

    /// <summary>Provider footnote under the charts (e.g. Gemini reports request counts, not tokens).</summary>
    public string ValueNote
    {
        get => _valueNote;
        private set
        {
            if (SetProperty(ref _valueNote, value))
            {
                OnPropertyChanged(nameof(HasValueNote));
            }
        }
    }

    public bool HasValueNote => !string.IsNullOrEmpty(_valueNote);

    public string StatsSummary
    {
        get => _statsSummary;
        set => SetProperty(ref _statsSummary, value);
    }

    public string AccountInfo
    {
        get => _accountInfo;
        set => SetProperty(ref _accountInfo, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public void UpdateFromSnapshot(ProviderSnapshot snapshot, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reference = now ?? DateTimeOffset.UtcNow;

        if (snapshot.Detection is not null)
        {
            Status = snapshot.Detection.State switch
            {
                DetectionState.Connected => "Connected",
                DetectionState.Limited => "Limited",
                DetectionState.Expired => "Expired",
                _ => "Not connected"
            };

            StatusDetail = !string.IsNullOrEmpty(snapshot.Detection.Hint)
                ? snapshot.Detection.Hint
                : snapshot.Detection.SourceLabel;
        }
        else
        {
            Status = snapshot.Ok ? "Connected" : (snapshot.Error is not null ? "Error" : "Not connected");
            StatusDetail = snapshot.Error ?? "";
        }

        AccountInfo = !string.IsNullOrEmpty(snapshot.Account) ? $"Account: {snapshot.Account}" : "";
        var configuredNow = snapshot.Detection is null
            ? snapshot.Ok
            : snapshot.Detection.State is DetectionState.Connected or DetectionState.Limited or DetectionState.Expired;
        // 1.3.3: the banner is for a failing, configured service; a history gap is reported where the charts would be.
        ErrorMessage = snapshot.Error ?? "";
        HistoryNote = string.IsNullOrEmpty(snapshot.HistoryError) ? "" : $"⚠ {snapshot.HistoryError}";
        ValueNote = snapshot.ValueNote;

        // Meters
        var meterList = new List<MeterDisplayItem>();
        foreach (var m in snapshot.Meters)
        {
            var effSev = m.EffectiveSeverity;
            var brush = AIMonitor.Presentation.Wpf.Theme.ThemeManager.SeverityBrush(effSev);

            var valText = m.Percent.HasValue ? $"{m.Percent.Value:F0}%" : "--";
            var (glyph, word) = SeverityLabels.Describe(effSev);

            meterList.Add(new MeterDisplayItem
            {
                Title = m.Title,
                Subtitle = m.Subtitle,
                Value = m.Percent ?? 0.0,
                ValueText = valText,
                Severity = effSev,
                GaugeBrush = brush,
                SeverityText = $"{glyph} {word}",
                ResetText = FormatResetText(m, reference),
                ResetCompact = FormatResetCompact(m, reference),
                IsFiveHour = m.Percent.HasValue && (m.Kind == "session"
                    || string.Equals(m.Subtitle.Trim(), "5-hour window", StringComparison.OrdinalIgnoreCase)),
            });
        }
        Meters = meterList;

        // History charts: stacked daily usage plus the by-model / by-project breakdowns.
        var history = snapshot.History;
        if (history is not null && history.Buckets.Count > 0)
        {
            var suffix = $" · last {history.Days} days";
            ChartBuckets = history.Buckets.OrderBy(b => b.Day).TakeLast(history.Days).ToList();
            ChartSeries = history.Series;
            ChartMetric = history.Metric;
            ChartTitle = "Usage per day" + suffix;
            ModelRows = history.ByModel;
            ModelTitle = "By model" + suffix;
            ProjectRows = history.ByProject;
            ProjectTitle = history.ProjectLabel + suffix;
            HasHistory = true;
        }
        else
        {
            ChartBuckets = [];
            ChartSeries = [];
            ModelRows = [];
            ProjectRows = [];
            HasHistory = false;
        }

        TabTitle = meterList.FirstOrDefault(m => m.ValueText != "--") is { } lead
            ? $"{DisplayName} {lead.ValueText}"
            : DisplayName;

        LastUpdated = snapshot.FetchedAt;
        IsConfigured = configuredNow;
        HasSnapshot = true;
        ShowSetupCard = !configuredNow && meterList.Count == 0;
        AccountLabel = !string.IsNullOrEmpty(snapshot.Account)
            ? snapshot.Account
            : ShowSetupCard ? $"{DisplayName} is not configured yet" : "";
        StatTiles = snapshot.Stats.Select(t => new StatTile(t.Label, t.Value, t.Detail)).ToList();

        // Stats summary
        if (snapshot.Stats.Count > 0)
        {
            StatsSummary = string.Join("  |  ", snapshot.Stats.Select(s => $"{s.Label}: {s.Value}"));
        }
    }

    private static string FormatResetCompact(Meter meter, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(meter.LockedReason))
        {
            return meter.LockedReason;
        }

        var display = ResetDisplayFormatter.Format(meter.ResetsAt, now, TimeZoneInfo.Local);
        if (display is null)
        {
            return "no reset scheduled";
        }

        return display.Countdown == "now" ? "resetting now" : $"resets in {display.Countdown}  ·  {display.LocalTime}";
    }

    private static string FormatResetText(Meter meter, DateTimeOffset now)
    {
        var display = ResetDisplayFormatter.Format(meter.ResetsAt, now, TimeZoneInfo.Local);
        if (display is null)
        {
            return string.IsNullOrEmpty(meter.LockedReason) ? "No reset scheduled" : meter.LockedReason;
        }

        return display.Countdown == "now"
            ? "Resetting now"
            : $"Resets in {display.Countdown} · {display.LocalTime}";
    }
}
