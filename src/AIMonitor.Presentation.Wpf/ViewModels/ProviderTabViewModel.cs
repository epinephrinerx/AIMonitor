using System.Globalization;
using System.Windows.Media;
using AIMonitor.Domain;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class MeterDisplayItem : ViewModelBase
{
    public string Title { get; init; } = "";

    /// <summary>The dashboard card's label: <see cref="Title"/> without the "Section · " prefix its
    /// section heading already shows. Widget and tray keep the full <see cref="Title"/>.</summary>
    public string GaugeTitle
    {
        get => string.IsNullOrEmpty(_gaugeTitle) ? Title : _gaugeTitle;
        init => _gaugeTitle = value;
    }

    private readonly string _gaugeTitle = "";

    public string Section { get; init; } = "";

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

    /// <summary>False when the service exposes no denominator ("--"); the tray then shows <see cref="Detail"/>.</summary>
    public bool HasPercent { get; init; }

    public string Detail { get; init; } = "";

    public DateTimeOffset? ResetsAt { get; init; }

    public string? LockedReason { get; init; }
}

/// <summary>One row of gauge cards under an optional heading. <see cref="Columns"/> is shared by
/// every section of a tab so cards line up across rows.</summary>
public sealed record MeterSection(string Heading, IReadOnlyList<MeterDisplayItem> Meters, int Columns);

/// <summary>A plain numeric readout shown in the stats row under the gauges.</summary>
public sealed record StatTile(string Label, string Value, string Detail);

public sealed class ProviderTabViewModel : ViewModelBase
{
    private string _status = "Checking...";
    private string _statusDetail = "";
    private Severity _severity = Severity.Normal;
    private IReadOnlyList<MeterDisplayItem> _meters = [];
    private IReadOnlyList<MeterSection> _meterSections = [];
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
    private SupplementaryDetailsViewModel? _supplementary;
    private bool _showSupplementary;
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

    public SupplementaryDetailsViewModel? Supplementary
    {
        get => _supplementary;
        private set
        {
            if (SetProperty(ref _supplementary, value))
            {
                OnPropertyChanged(nameof(SupplementaryRows));
                OnPropertyChanged(nameof(ActivityPills));
                OnPropertyChanged(nameof(ResetBadge));
                OnPropertyChanged(nameof(ResetSummary));
                OnPropertyChanged(nameof(ResetDetails));
                OnPropertyChanged(nameof(ResetTitle));
                OnPropertyChanged(nameof(HasSupplementaryRows));
                OnPropertyChanged(nameof(HasSupplementaryActivity));
                OnPropertyChanged(nameof(ActivityTitle));
            }
        }
    }

    public bool ShowSupplementary
    {
        get => _showSupplementary;
        private set => SetProperty(ref _showSupplementary, value);
    }

    public IReadOnlyList<SupplementaryRowViewModel> SupplementaryRows => _supplementary?.Rows ?? [];
    public IReadOnlyList<SupplementaryPillViewModel> ActivityPills => _supplementary?.ActivityPills ?? [];
    public string ResetBadge => _supplementary?.ResetBadge ?? "";
    public string ResetSummary => _supplementary?.ResetSummary ?? "";
    public string ResetDetails => _supplementary?.ResetDetails ?? "";
    public string ResetTitle => _supplementary?.ResetTitle ?? "Rate Limit Resets";
    public bool HasSupplementaryRows => _supplementary?.HasRows ?? false;
    public bool HasSupplementaryActivity => _supplementary?.HasActivity ?? false;
    public string ActivityTitle => _supplementary?.ActivityTitle ?? "Activity";

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
        set
        {
            if (SetProperty(ref _meters, value))
            {
                MeterSections = BuildSections(value);
            }
        }
    }

    private static IReadOnlyList<MeterSection> BuildSections(IReadOnlyList<MeterDisplayItem> meters)
    {
        var groups = meters.GroupBy(m => m.Section, StringComparer.Ordinal).ToList();
        var columns = groups.Count == 0 ? 0 : groups.Max(g => g.Count());
        return groups.Select(g => new MeterSection(g.Key, g.ToList(), columns)).ToList();
    }

    /// <summary><see cref="Meters"/> grouped by <see cref="Meter.Section"/> in first-seen order;
    /// a provider without sections yields one unheaded row, as before.</summary>
    public IReadOnlyList<MeterSection> MeterSections
    {
        get => _meterSections;
        private set => SetProperty(ref _meterSections, value);
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
                GaugeTitle = m.Section.Length > 0 && m.Title.StartsWith(m.Section + " · ", StringComparison.Ordinal)
                    ? m.Title[(m.Section.Length + 3)..]
                    : m.Title,
                Section = m.Section,
                Subtitle = m.Subtitle,
                Value = m.Percent ?? 0.0,
                ValueText = valText,
                Severity = effSev,
                GaugeBrush = brush,
                SeverityText = $"{glyph} {word}",
                ResetText = FormatResetText(m, reference),
                ResetCompact = FormatResetCompact(m, reference),
                HasPercent = m.Percent.HasValue,
                Detail = m.Detail,
                ResetsAt = m.ResetsAt,
                LockedReason = m.LockedReason,
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

        var isClaude = string.Equals(ProviderId, "claude", StringComparison.OrdinalIgnoreCase);
        var isOpenAi = string.Equals(ProviderId, "openai", StringComparison.OrdinalIgnoreCase);

        if (isClaude || isOpenAi)
        {
            ShowSupplementary = configuredNow || snapshot.Stats.Any(IsSupplementaryStat);
            var wasExpanded = Supplementary?.IsResetDetailsExpanded ?? false;
            Supplementary = BuildSupplementary(snapshot, isClaude, isOpenAi);
            Supplementary.IsResetDetailsExpanded = wasExpanded;
        }
        else
        {
            ShowSupplementary = false;
            Supplementary = null;
        }

        StatTiles = snapshot.Stats.Where(s => !ShowSupplementary || !IsSupplementaryStat(s)).Select(t => new StatTile(t.Label, t.Value, t.Detail)).ToList();

        // Stats summary
        if (snapshot.Stats.Count > 0)
        {
            StatsSummary = string.Join("  |  ", snapshot.Stats.Select(s => $"{s.Label}: {s.Value}"));
        }
        else
        {
            StatsSummary = "";
        }
    }

    private static bool IsSupplementaryStat(Stat s)
    {
        var label = s.Label;
        if (label.Equals("Plan", StringComparison.OrdinalIgnoreCase) || label.EndsWith("· Plan", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.Equals("Credits", StringComparison.OrdinalIgnoreCase) || label.EndsWith("· Credits", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.Equals("Limit reason", StringComparison.OrdinalIgnoreCase) || label.EndsWith("· Limit reason", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.StartsWith("Rate limit reset", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.Equals("Current streak", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Longest streak", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Longest turn", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private SupplementaryDetailsViewModel BuildSupplementary(ProviderSnapshot snapshot, bool isClaude, bool isOpenAi)
    {
        string resetTitle;
        string resetBadge;
        string resetSummary;
        string resetDetails;

        if (isClaude)
        {
            resetTitle = "Limit Resets (Claude)";
            resetBadge = "Not verified";
            resetSummary = "AIMonitor cannot verify reset offers here. Check Settings > Usage on Claude web/Desktop.";
            resetDetails = "Official Claude Limit Reset Procedure\n\n"
                + "Claude limit resets are managed directly within the official Anthropic interface:\n"
                + "1. Open Claude on Web (claude.ai) or Claude Desktop (not mobile app, IDE extension, or terminal).\n"
                + "2. Navigate to Settings > Usage.\n"
                + "3. Under Resets, check if an offer is present.\n"
                + "4. Click Reset for free > Confirm.\n\n"
                + "Rules: Redemption is irreversible. Normal weekly schedule remains unchanged. Extra usage credits are distinct. Shared account benefits Claude Code and mobile.\n\n"
                + "Reference: support.claude.com/en/articles/17007452";
        }
        else
        {
            resetTitle = "Rate Limit Resets (Codex)";
            var resetStat = snapshot.Stats.FirstOrDefault(s => s.Label.StartsWith("Rate limit reset", StringComparison.OrdinalIgnoreCase));
            if (resetStat is not null)
            {
                if (long.TryParse(resetStat.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0)
                {
                    if (count > 0)
                    {
                        resetBadge = $"Available: {count}";
                        var expiry = !string.IsNullOrEmpty(resetStat.Detail) ? resetStat.Detail : "Next expiry unknown";
                        resetSummary = $"{count} reset rights available · {expiry}. These are separate from paid credits.";
                    }
                    else
                    {
                        resetBadge = "0 Credits";
                        resetSummary = "0 Rate Limit Reset Credits · Confirmed no reset credits available on this account (count = 0).";
                    }

                    resetDetails = "Codex Reset Details\n\n"
                        + "Earned reset rights are reported via Codex App Server account/rateLimits/read. When availableCount > 0, users may restore a capped quota window.\n\n"
                        + "Read-Only Notice: AIMonitor is a read-only monitor. Reset redemption is managed via official client. No redemption action is executed here.\n\n"
                        + "Reference: learn.chatgpt.com/docs/app-server";
                }
                else
                {
                    resetBadge = "Not verified";
                    resetSummary = "Reset Credits Status: Not verified · Server returned malformed or unconfirmed status.";
                    resetDetails = "Codex Reset Details\n\n"
                        + "Server returned malformed or unconfirmed status. Entitlement cannot be verified by AIMonitor.\n\n"
                        + "Read-Only Notice: AIMonitor is a read-only monitor. No redemption action is executed here.\n\n"
                        + "Reference: learn.chatgpt.com/docs/app-server";
                }
            }
            else
            {
                resetBadge = "Not verified";
                resetSummary = "Reset Credits Status: Not verified · No confirmed reset data (not zero). Entitlement cannot be verified by AIMonitor.";
                resetDetails = "Codex Reset Details\n\n"
                    + "No confirmed reset data (not zero). Entitlement cannot be verified by AIMonitor.\n\n"
                    + "Read-Only Notice: AIMonitor is a read-only monitor. No redemption action is executed here.\n\n"
                    + "Reference: learn.chatgpt.com/docs/app-server";
            }
        }

        var isApiMode = isOpenAi && snapshot.Detection?.SourceId is "manual" or "env";
        if (isApiMode)
        {
            resetTitle = "Codex reset rights";
            resetBadge = "Not available in API mode";
            resetSummary = "Organization API usage does not report ChatGPT/Codex reset rights.";
            resetDetails = "Use your Codex ChatGPT login to view earned reset rights. AIMonitor performs no redemption.";
        }

        var rows = new List<SupplementaryRowViewModel>();

        foreach (var s in snapshot.Stats)
        {
            if (s.Label.Equals("Plan", StringComparison.OrdinalIgnoreCase) || s.Label.EndsWith("· Plan", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = s.Label,
                    Value = s.Value,
                    Detail = s.Detail,
                    Badge = "Plan",
                    Group = "Plan"
                });
            }
            else if (s.Label.Equals("Credits", StringComparison.OrdinalIgnoreCase) || s.Label.EndsWith("· Credits", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = s.Label,
                    Value = s.Value,
                    Detail = s.Detail,
                    Badge = "Credits",
                    Group = "Credits"
                });
            }
            else if (s.Label.Equals("Limit reason", StringComparison.OrdinalIgnoreCase) || s.Label.EndsWith("· Limit reason", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = s.Label,
                    Value = s.Value,
                    Detail = s.Detail,
                    Badge = "Limit",
                    Group = "Limit reason"
                });
            }
        }

        if (!rows.Any(r => r.Group == "Plan"))
        {
            if (isClaude)
            {
                rows.Insert(0, new SupplementaryRowViewModel
                {
                    Label = "Plan",
                    Value = "Not reported",
                    Detail = "Check your plan in Claude",
                    Badge = "Plan",
                    Group = "Plan"
                });
            }
            else if (isOpenAi)
            {
                rows.Insert(0, new SupplementaryRowViewModel
                {
                    Label = "Plan",
                    Value = "Not reported",
                    Detail = "Plan not reported via API",
                    Badge = "Plan",
                    Group = "Plan"
                });
            }
        }

        if (!rows.Any(r => r.Group == "Credits"))
        {
            if (isClaude)
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = "Workspace Credits",
                    Value = "Not verified",
                    Detail = "Check Claude for extra usage balance",
                    Badge = "Credits",
                    Group = "Credits"
                });
            }
            else if (isOpenAi)
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = "Workspace Credits",
                    Value = "Not reported",
                    Detail = "Workspace credit balance unavailable",
                    Badge = "Credits",
                    Group = "Credits"
                });
            }
        }

        // Activity
        var activityPills = new List<SupplementaryPillViewModel>();
        string activityTitle;
        string activityNote;

        var currentStreak = snapshot.Stats.FirstOrDefault(s => s.Label.Equals("Current streak", StringComparison.OrdinalIgnoreCase));
        var longestStreak = snapshot.Stats.FirstOrDefault(s => s.Label.Equals("Longest streak", StringComparison.OrdinalIgnoreCase));
        var longestTurn = snapshot.Stats.FirstOrDefault(s => s.Label.Equals("Longest turn", StringComparison.OrdinalIgnoreCase));

        if (currentStreak is not null || longestStreak is not null || longestTurn is not null)
        {
            activityTitle = "Codex Activity";
            activityNote = "From account/usage/read summary · Metrics hide when null.";
            if (currentStreak is not null) activityPills.Add(new SupplementaryPillViewModel { Label = "Current streak", Value = currentStreak.Value });
            if (longestStreak is not null) activityPills.Add(new SupplementaryPillViewModel { Label = "Longest streak", Value = longestStreak.Value });
            if (longestTurn is not null) activityPills.Add(new SupplementaryPillViewModel { Label = "Longest turn", Value = longestTurn.Value });
        }
        else if (isClaude)
        {
            activityTitle = "Claude Telemetry Notes";
            activityNote = "Resets apply to 5-hour session or weekly quota as indicated by offer. Check Settings > Usage in Claude.";
            activityPills.Add(new SupplementaryPillViewModel { Label = "Scope", Value = "Session / Weekly" });
            activityPills.Add(new SupplementaryPillViewModel { Label = "Cross-client", Value = "Web / Desktop / Code" });
        }
        else
        {
            activityTitle = "Codex Activity";
            activityNote = "Activity telemetry not reported";
        }

        if (isApiMode)
        {
            activityTitle = "Account activity";
            activityNote = "Codex activity is not available in organization API mode.";
        }

        return new SupplementaryDetailsViewModel
        {
            ProviderId = ProviderId,
            ResetTitle = resetTitle,
            ResetBadge = resetBadge,
            ResetSummary = resetSummary,
            ResetDetails = resetDetails,
            Rows = rows,
            ActivityPills = activityPills,
            ActivityTitle = activityTitle,
            ActivityNote = activityNote
        };
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
