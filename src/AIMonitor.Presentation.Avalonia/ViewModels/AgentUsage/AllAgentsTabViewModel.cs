using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// View-model for the "All agents" ccusage tab.
/// </summary>
public sealed class AllAgentsTabViewModel : ObservableObject
{
    private AgentUsagePeriod? _today;
    private AgentUsagePeriod? _thisMonth;
    private IReadOnlyList<AgentUsagePeriod> _days = [];
    private IReadOnlyList<AgentUsagePeriod> _months = [];
    private IReadOnlyList<AgentUsageSessionLine> _sessions = [];
    private string _statusText = "Loading ccusage…";
    private string _errorText = "";
    private bool _showInstallHint;
    private string _pricingText = "";
    private string? _pricingToolTip;
    private bool _hasDays;
    private bool _hasMonths;
    private bool _hasSessions;

    public string TabTitle => "All agents";
    public string TabId => "all-agents";

    public AgentUsagePeriod? Today
    {
        get => _today;
        private set => Set(ref _today, value);
    }

    public AgentUsagePeriod? ThisMonth
    {
        get => _thisMonth;
        private set => Set(ref _thisMonth, value);
    }

    public IReadOnlyList<AgentUsagePeriod> Days
    {
        get => _days;
        private set => Set(ref _days, value);
    }

    public IReadOnlyList<AgentUsagePeriod> Months
    {
        get => _months;
        private set => Set(ref _months, value);
    }

    public IReadOnlyList<AgentUsageSessionLine> Sessions
    {
        get => _sessions;
        private set => Set(ref _sessions, value);
    }

    public bool HasDays
    {
        get => _hasDays;
        private set => Set(ref _hasDays, value);
    }

    public bool HasMonths
    {
        get => _hasMonths;
        private set => Set(ref _hasMonths, value);
    }

    public bool HasSessions
    {
        get => _hasSessions;
        private set => Set(ref _hasSessions, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value))
            {
                Raise(nameof(HasError));
                Raise(nameof(ShowError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool ShowError => HasError && !ShowInstallHint;

    public bool ShowContent => !ShowInstallHint;

    public bool ShowInstallHint
    {
        get => _showInstallHint;
        private set
        {
            if (Set(ref _showInstallHint, value))
            {
                Raise(nameof(ShowContent));
                Raise(nameof(ShowError));
            }
        }
    }

    public string PricingText
    {
        get => _pricingText;
        private set => Set(ref _pricingText, value);
    }

    public string? PricingToolTip
    {
        get => _pricingToolTip;
        private set => Set(ref _pricingToolTip, value);
    }

    public void Update(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        DateOnly today,
        int rangeDays,
        TimeZoneInfo zone)
    {
        Today = lastGood?.Daily.FirstOrDefault(d => d.Date == today) is { } day
            ? AgentUsagePresenter.BuildDay(day)
            : null;

        ThisMonth = lastGood?.Monthly.FirstOrDefault(m => m.Year == today.Year && m.Month == today.Month) is { } month
            ? AgentUsagePresenter.BuildMonth(month)
            : null;

        var cutoff = today.AddDays(-(rangeDays - 1));
        Days = lastGood?.Daily
                   .Where(d => d.Date >= cutoff && d.Date <= today)
                   .OrderByDescending(d => d.Date)
                   .Select(AgentUsagePresenter.BuildDay)
                   .ToList()
               ?? [];
        HasDays = Days.Count > 0;

        Months = lastGood?.Monthly
                     .OrderByDescending(m => m.Year)
                     .ThenByDescending(m => m.Month)
                     .Select(AgentUsagePresenter.BuildMonth)
                     .ToList()
                 ?? [];
        HasMonths = Months.Count > 0;

        Sessions = lastGood?.Sessions
                       .Take(20)
                       .Select(s => AgentUsagePresenter.BuildSession(s, zone))
                       .ToList()
                   ?? [];
        HasSessions = Sessions.Count > 0;

        if (lastGood is null && lastError is null)
        {
            StatusText = "Loading ccusage…";
            PricingText = "";
            PricingToolTip = null;
        }
        else
        {
            var parts = new List<string>();
            if (lastGood is not null)
            {
                var localTime = TimeZoneInfo.ConvertTime(lastGood.RetrievedAt, zone);
                parts.Add($"Updated {localTime:HH:mm}");
                parts.Add(AgentUsagePresenter.PricingText(lastGood.Pricing));
                if (lastGood.SkippedRows > 0)
                {
                    parts.Add($"{lastGood.SkippedRows} rows skipped");
                }

                PricingText = AgentUsagePresenter.PricingText(lastGood.Pricing);
                PricingToolTip = AgentUsagePresenter.PricingToolTip(lastGood.Pricing);
            }
            else
            {
                parts.Add("Update failed");
                PricingText = "";
                PricingToolTip = null;
            }

            StatusText = string.Join(" · ", parts);
        }

        if (lastError is null)
        {
            ErrorText = "";
            ShowInstallHint = false;
        }
        else
        {
            var message = AgentUsagePresenter.ErrorText(lastError.Kind, lastError.Message);
            if (lastGood is not null)
            {
                var localTime = TimeZoneInfo.ConvertTime(lastGood.RetrievedAt, zone);
                message += $" Showing data from {localTime:HH:mm}.";
            }

            ErrorText = message;
            ShowInstallHint = lastError.Kind == AgentUsageErrorKind.NotInstalled && lastGood is null;
        }
    }
}
