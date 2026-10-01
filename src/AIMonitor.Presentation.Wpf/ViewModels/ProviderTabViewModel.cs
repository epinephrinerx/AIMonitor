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
}

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

    public ProviderTabViewModel(string providerId, string displayName)
    {
        ProviderId = providerId;
        DisplayName = displayName;
    }

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
        set => SetProperty(ref _errorMessage, value);
    }

    public void UpdateFromSnapshot(ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

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
            var brush = effSev switch
            {
                Severity.Critical => (System.Windows.Application.Current?.Resources["SeverityCriticalBrush"] as Brush) ?? Brushes.Red,
                Severity.VeryHigh or Severity.High => (System.Windows.Application.Current?.Resources["SeverityWarningBrush"] as Brush) ?? Brushes.Orange,
                _ => (System.Windows.Application.Current?.Resources["SeverityNormalBrush"] as Brush) ?? Brushes.Teal
            };

            var valText = m.Percent.HasValue ? $"{m.Percent.Value:F0}%" : "--";
            var sub = m.ResetsAt.HasValue ? $"Resets in {FormatReset(m.ResetsAt.Value)}" : m.Subtitle;

            meterList.Add(new MeterDisplayItem
            {
                Title = m.Title,
                Subtitle = sub,
                Value = m.Percent ?? 0.0,
                ValueText = valText,
                Severity = effSev,
                GaugeBrush = brush
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

        // Stats summary
        if (snapshot.Stats.Count > 0)
        {
            StatsSummary = string.Join("  |  ", snapshot.Stats.Select(s => $"{s.Label}: {s.Value}"));
        }
    }

    private static string FormatReset(DateTimeOffset resetTime)
    {
        var diff = resetTime - DateTimeOffset.UtcNow;
        if (diff <= TimeSpan.Zero) return "now";
        if (diff.TotalHours >= 24) return $"{(int)diff.TotalDays}d {diff.Hours}h";
        return $"{diff.Hours}h {diff.Minutes}m";
    }
}
