using System.Windows.Media;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Controls;
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
}

/// <summary>A plain numeric readout shown in the stats row under the gauges.</summary>
public sealed record StatTile(string Label, string Value, string Detail);

public sealed class ProviderTabViewModel : ViewModelBase
{
    private string _status = "Checking...";
    private string _statusDetail = "";
    private Severity _severity = Severity.Normal;
    private IReadOnlyList<MeterDisplayItem> _meters = [];
    private IReadOnlyList<DailyChartBar> _dailyUsage = [];
    private string _statsSummary = "";
    private string _accountInfo = "";
    private string _errorMessage = "";
    private IReadOnlyList<StatTile> _statTiles = [];
    private string _tabTitle;
    private string _accountLabel = "";
    private bool _showSetupCard;

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

    public IReadOnlyList<DailyChartBar> DailyUsage
    {
        get => _dailyUsage;
        set => SetProperty(ref _dailyUsage, value);
    }

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
        ErrorMessage = snapshot.Error ?? snapshot.HistoryError ?? "";

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
            });
        }
        Meters = meterList;

        // Daily chart items (PAR-015: metrics and date ranges)
        if (snapshot.History is not null && snapshot.History.Buckets.Count > 0)
        {
            var days = snapshot.History.Days > 0 ? snapshot.History.Days : 14;
            var isCurrency = snapshot.History.Metric.Contains("value", StringComparison.OrdinalIgnoreCase) ||
                             snapshot.History.Metric.Contains("cost", StringComparison.OrdinalIgnoreCase) ||
                             snapshot.History.Metric.Contains("$", StringComparison.OrdinalIgnoreCase);

            DailyUsage = snapshot.History.Buckets
                .OrderBy(b => b.Day)
                .TakeLast(days)
                .Select(b => new DailyChartBar(
                    b.Day.ToString("MM/dd", System.Globalization.CultureInfo.InvariantCulture),
                    b.Total,
                    isCurrency ? $"${b.Total:N2}" : $"{b.Total:N0}"))
                .ToList();
        }

        TabTitle = meterList.FirstOrDefault(m => m.ValueText != "--") is { } lead
            ? $"{DisplayName} {lead.ValueText}"
            : DisplayName;

        var configured = snapshot.Detection is null
            ? snapshot.Ok
            : snapshot.Detection.State is DetectionState.Connected or DetectionState.Limited or DetectionState.Expired;
        ShowSetupCard = !configured && meterList.Count == 0;
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
