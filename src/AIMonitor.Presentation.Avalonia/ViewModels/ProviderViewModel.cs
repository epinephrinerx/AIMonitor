using System.Collections.ObjectModel;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// One row of gauge cards under an optional heading. <see cref="Columns"/> is shared by every
/// section of a service so the cards line up across rows rather than each row sizing itself.
/// </summary>
public sealed record MeterSection(string Heading, IReadOnlyList<MeterViewModel> Meters, int Columns)
{
    public bool HasHeading => Heading.Length > 0;
}

/// <summary>A plain readout in the row under the gauges.</summary>
public sealed record StatTile(string Label, string Value, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>One service's readings as the dashboard shows them.</summary>
public sealed class ProviderViewModel : ObservableObject
{
    private readonly string _defaultSetupHint;
    private string _account = string.Empty;
    private string _errorMessage = string.Empty;
    private string _historyNote = string.Empty;
    private string _setupHint;
    private string _valueNote = string.Empty;
    private string _tabTitle;
    private bool _configured;
    private bool _hasHistory;
    private DateTimeOffset? _fetchedAt;
    private IReadOnlyList<UsageHistoryBucket> _chartBuckets = [];
    private IReadOnlyList<string> _chartSeries = [];
    private IReadOnlyList<UsageHistoryBreakdown> _modelRows = [];
    private IReadOnlyList<UsageHistoryBreakdown> _projectRows = [];
    private string _chartMetric = "Total tokens";
    private string _chartTitle = "Usage per day";
    private string _modelTitle = "By model";
    private string _projectTitle = "By project";

    public ProviderViewModel(string providerId, string title)
    {
        ProviderId = providerId;
        Title = title;
        _tabTitle = title;
        // The standing advice for this service, the way the Windows build reads it from its own
        // catalogue: a service that has never been refreshed still has something useful to say.
        _defaultSetupHint = ProviderCatalog.For(providerId).SetupHint;
        _setupHint = _defaultSetupHint;
    }

    public string ProviderId { get; }

    public string Title { get; }

    public ObservableCollection<MeterViewModel> Meters { get; } = [];

    /// <summary><see cref="Meters"/> grouped by section in first-seen order; a service without
    /// sections yields one unheaded row.</summary>
    public ObservableCollection<MeterSection> MeterSections { get; } = [];

    public ObservableCollection<StatTile> StatTiles { get; } = [];

    public bool HasStats => StatTiles.Count > 0;

    /// <summary>"Claude 58%" — the tab carries the lead reading, as on Windows.</summary>
    public string TabTitle
    {
        get => _tabTitle;
        private set => Set(ref _tabTitle, value);
    }

    public bool Configured
    {
        get => _configured;
        private set
        {
            if (Set(ref _configured, value)) Raise(nameof(ShowSetupHint));
        }
    }

    /// <summary>The signed-in account, or why there is none to show.</summary>
    public string Account
    {
        get => _account;
        private set
        {
            if (Set(ref _account, value)) Raise(nameof(HasAccount));
        }
    }

    public bool HasAccount => _account.Length > 0;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _errorMessage.Length > 0;

    /// <summary>
    /// A history failure, reported where the charts would be rather than in the banner at the top:
    /// it must never look like the live quota failed too.
    /// </summary>
    public string HistoryNote
    {
        get => _historyNote;
        private set
        {
            if (Set(ref _historyNote, value)) Raise(nameof(HasHistoryNote));
        }
    }

    public bool HasHistoryNote => _historyNote.Length > 0;

    public string SetupHint
    {
        get => _setupHint;
        private set
        {
            if (Set(ref _setupHint, value)) Raise(nameof(ShowSetupHint));
        }
    }

    /// <summary>"Claude is not set up yet" — the heading above the hint.</summary>
    public string SetupTitle => $"{Title} is not set up yet";

    /// <summary>
    /// Only while the service has nothing to show. Once a reading arrives the card has done its
    /// job, and leaving it up would tell somebody who is signed in to go and sign in.
    /// </summary>
    public bool ShowSetupHint => _setupHint.Length > 0 && !_configured && Meters.Count == 0;

    public string ValueNote
    {
        get => _valueNote;
        private set
        {
            if (Set(ref _valueNote, value)) Raise(nameof(HasValueNote));
        }
    }

    public bool HasValueNote => _valueNote.Length > 0;

    public DateTimeOffset? FetchedAt
    {
        get => _fetchedAt;
        private set => Set(ref _fetchedAt, value);
    }

    /// <summary>Day buckets for the stacked chart, oldest first.</summary>
    public IReadOnlyList<UsageHistoryBucket> ChartBuckets
    {
        get => _chartBuckets;
        private set => Set(ref _chartBuckets, value);
    }

    /// <summary>The models stacked in each column, in the order they are drawn and coloured.</summary>
    public IReadOnlyList<string> ChartSeries
    {
        get => _chartSeries;
        private set => Set(ref _chartSeries, value);
    }

    public IReadOnlyList<UsageHistoryBreakdown> ModelRows
    {
        get => _modelRows;
        private set
        {
            if (Set(ref _modelRows, value)) Raise(nameof(HasModelRows));
        }
    }

    public IReadOnlyList<UsageHistoryBreakdown> ProjectRows
    {
        get => _projectRows;
        private set
        {
            if (Set(ref _projectRows, value)) Raise(nameof(HasProjectRows));
        }
    }

    /// <summary>What the numbers count — tokens, output tokens, or equivalent value.</summary>
    public string ChartMetric
    {
        get => _chartMetric;
        private set => Set(ref _chartMetric, value);
    }

    public string ChartTitle
    {
        get => _chartTitle;
        private set => Set(ref _chartTitle, value);
    }

    public string ModelTitle
    {
        get => _modelTitle;
        private set => Set(ref _modelTitle, value);
    }

    /// <summary>The service's own word for a project: Claude calls them projects, Codex does not.</summary>
    public string ProjectTitle
    {
        get => _projectTitle;
        private set => Set(ref _projectTitle, value);
    }

    public bool HasHistory
    {
        get => _hasHistory;
        private set => Set(ref _hasHistory, value);
    }

    public bool HasModelRows => _modelRows.Count > 0;

    public bool HasProjectRows => _projectRows.Count > 0;

    /// <summary>
    /// A null snapshot means this refresh skipped the service (the user switched monitoring off),
    /// which must not be shown as a failure or wipe the last good readings.
    /// </summary>
    public void Apply(ProviderSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            Configured = false;
            ErrorMessage = string.Empty;
            return;
        }

        Configured = snapshot.Configured;
        ErrorMessage = snapshot.Error ?? string.Empty;
        SetupHint = snapshot.SetupHint.Length > 0 ? snapshot.SetupHint : _defaultSetupHint;
        ValueNote = snapshot.ValueNote;
        FetchedAt = snapshot.FetchedAt;
        HistoryNote = string.IsNullOrEmpty(snapshot.HistoryError) ? string.Empty : $"⚠ {snapshot.HistoryError}";

        // Replaced wholesale rather than merged: a window the service stopped reporting must
        // disappear instead of lingering with a stale reading.
        var now = DateTimeOffset.Now;
        Meters.Clear();
        foreach (var meter in snapshot.Meters)
        {
            Meters.Add(new MeterViewModel(meter, now, TimeZoneInfo.Local));
        }

        RebuildSections();

        StatTiles.Clear();
        foreach (var stat in snapshot.Stats)
        {
            StatTiles.Add(new StatTile(stat.Label, stat.Value, stat.Detail));
        }

        ApplyHistory(snapshot.History);

        var lead = Meters.FirstOrDefault(meter => meter.HasPercent);
        TabTitle = lead is null ? Title : $"{Title} {lead.ValueText}";

        Account = snapshot.Account.Length > 0
            ? snapshot.Account
            : ShowSetupHint ? $"{Title} is not configured yet" : string.Empty;

        Raise(nameof(HasStats));
        Raise(nameof(ShowSetupHint));
    }

    private void ApplyHistory(UsageHistory? history)
    {
        if (history is null || history.Buckets.Count == 0)
        {
            ChartBuckets = [];
            ChartSeries = [];
            ModelRows = [];
            ProjectRows = [];
            HasHistory = false;
            return;
        }

        var suffix = $" · last {history.Days} days";
        ChartBuckets = [.. history.Buckets.OrderBy(bucket => bucket.Day).TakeLast(history.Days)];
        ChartSeries = history.Series;
        ChartMetric = history.Metric;
        ChartTitle = "Usage per day" + suffix;
        ModelRows = history.ByModel;
        ModelTitle = "By model" + suffix;
        ProjectRows = history.ByProject;
        ProjectTitle = history.ProjectLabel + suffix;
        HasHistory = true;
    }

    private void RebuildSections()
    {
        MeterSections.Clear();
        var groups = Meters.GroupBy(meter => meter.Section, StringComparer.Ordinal).ToList();
        // One column count for every row, so a two-card row and a three-card row still line up.
        var columns = groups.Count == 0 ? 0 : groups.Max(group => group.Count());
        foreach (var group in groups)
        {
            MeterSections.Add(new MeterSection(group.Key, [.. group], columns));
        }
    }
}

/// <summary>One usage window, as one gauge card.</summary>
public sealed class MeterViewModel
{
    public MeterViewModel(Meter meter)
        : this(meter, DateTimeOffset.Now, TimeZoneInfo.Local)
    {
    }

    internal MeterViewModel(Meter meter, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(timeZone);

        Kind = meter.Kind;
        Title = meter.Title;
        Subtitle = meter.Subtitle;
        Section = meter.Section;
        Detail = meter.Detail;
        Percent = meter.Percent;
        // The worse of the local threshold and what the service itself said, so a window sitting at
        // 95% reads as critical even when the service reports nothing unusual.
        Severity = meter.EffectiveSeverity;
        LockedReason = meter.LockedReason ?? string.Empty;
        ResetsAt = meter.ResetsAt;

        var described = SeverityLabels.Describe(Severity);
        SeverityText = $"{described.Glyph} {described.Label}";
        ResetText = FormatReset(meter, now, timeZone);
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>
    /// The card's own label: <see cref="Title"/> without the "Section · " prefix the heading above
    /// it already carries.
    /// </summary>
    public string GaugeTitle =>
        Section.Length > 0 && Title.StartsWith(Section + " · ", StringComparison.Ordinal)
            ? Title[(Section.Length + 3)..]
            : Title;

    public string Subtitle { get; }

    public string Section { get; }

    public string Detail { get; }

    /// <summary>Null when the service exposes no denominator; no percentage is ever invented.</summary>
    public double? Percent { get; }

    public double PercentOrZero => Percent ?? 0;

    public bool HasPercent => Percent.HasValue;

    /// <summary>"58%", or the two dashes the Windows build shows for a window with no denominator.</summary>
    public string ValueText => Percent.HasValue
        ? Percent.Value.ToString("F0", System.Globalization.CultureInfo.CurrentCulture) + "%"
        : "--";

    public Severity Severity { get; }

    /// <summary>Glyph plus word, so colour never carries the severity on its own.</summary>
    public string SeverityText { get; }

    /// <summary>"Resets in 2h 5m · 17:50", or why there is no reset to show.</summary>
    public string ResetText { get; }

    public string LockedReason { get; }

    public bool IsLocked => LockedReason.Length > 0;

    public DateTimeOffset? ResetsAt { get; }

    public bool HasSubtitle => Subtitle.Length > 0;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>
    /// The session window, which is the one the menu-bar icon draws: one tile holds one number, and
    /// the five-hour window is the one people watch. Matches the rule the Windows tray uses.
    /// </summary>
    public bool IsFiveHour =>
        Percent.HasValue
        && (Kind == "session"
            || string.Equals(Subtitle.Trim(), "5-hour window", StringComparison.OrdinalIgnoreCase));

    // Avalonia cannot bind the Classes collection itself, so each class is toggled by its own
    // boolean through Classes.<name>.
    public bool IsNormal => Severity == Severity.Normal;

    public bool IsHigh => Severity == Severity.High;

    public bool IsVeryHigh => Severity == Severity.VeryHigh;

    public bool IsCritical => Severity == Severity.Critical;

    private static string FormatReset(Meter meter, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var display = ResetDisplayFormatter.Format(meter.ResetsAt, now, timeZone);
        if (display is null)
        {
            return string.IsNullOrEmpty(meter.LockedReason) ? "No reset scheduled" : meter.LockedReason;
        }

        return display.Countdown == "now"
            ? "Resetting now"
            : $"Resets in {display.Countdown} · {display.LocalTime}";
    }
}
