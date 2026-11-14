using System.Globalization;
using System.Windows.Input;
using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>One service's "monitor this" switch on the settings page.</summary>
public sealed class ProviderToggle(string providerId, string displayName, bool enabled) : ObservableObject
{
    private bool _enabled = enabled;

    public string ProviderId { get; } = providerId;

    public string DisplayName { get; } = displayName;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }
}

/// <summary>
/// The settings page.
///
/// Nothing is applied while the page is open: every field edits a copy, Save writes that copy, and
/// Cancel throws it away. That is what makes "Cancel puts everything back" true by construction
/// rather than by remembering to undo each field.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsSession _session;
    private AppSettings _original;
    private string _theme;
    private bool _startAtLogin;
    private bool _minimizeToMenuBar;
    private bool _showMenuBarExtra;
    private string _refreshIntervalText;
    private string _refreshIntervalError = string.Empty;
    private double _widgetOpacity;
    private bool _widgetAlwaysOnTop;
    private bool _widgetRotationEnabled;
    private int _chartRangeDays;
    private bool _isSaving;
    private bool _hasBudgetError;

    /// <param name="knownAgents">Agents ccusage has reported, so each gets a budget row; OpenCode always does.</param>
    public SettingsViewModel(SettingsSession session, IReadOnlyList<string>? knownAgents = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _original = session.Current;

        Providers = [.. ProviderCatalog.All.Select(provider => new ProviderToggle(
            provider.Id,
            provider.DisplayName,
            _original.Providers.TryGetValue(provider.Id, out var preference) ? preference.Enabled : true))];

        _theme = _original.Theme;
        _startAtLogin = _original.StartWithWindows;
        _minimizeToMenuBar = _original.MinimizeToTray;
        _showMenuBarExtra = _original.ShowTrayIcon;
        _refreshIntervalText = _original.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        _widgetOpacity = _original.WidgetOpacity;
        _widgetAlwaysOnTop = _original.WidgetAlwaysOnTop;
        _widgetRotationEnabled = _original.WidgetRotationEnabled;
        _chartRangeDays = _original.ChartRangeDays;

        BudgetRows = CreateBudgetRows(knownAgents);
        _hasBudgetError = BudgetRows.Any(row => row.HasError);

        SaveCommand = new AsyncCommand(SaveAsync, () => !HasIntervalError && !HasBudgetError);
        CancelCommand = new RelayCommand(Cancel);
    }

    public event Action<bool>? RequestClose;

    public event Action<string>? SaveFailed;

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public IReadOnlyList<ProviderToggle> Providers { get; }

    /// <summary>Monthly budget per agent (R-015), OpenCode first, then by name. Empty means no budget.</summary>
    public IReadOnlyList<AgentBudgetRowViewModel> BudgetRows { get; }

    public bool HasBudgetError
    {
        get => _hasBudgetError;
        private set
        {
            if (Set(ref _hasBudgetError, value)) (SaveCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<string> Themes { get; } = ["system", "light", "dark"];

    public IReadOnlyList<int> ChartRanges { get; } = [7, 14, 30, 90];

    public string Theme
    {
        get => _theme;
        set => Set(ref _theme, value);
    }

    /// <summary>Named for what macOS calls it; the stored field is shared with the Windows build.</summary>
    public bool StartAtLogin
    {
        get => _startAtLogin;
        set => Set(ref _startAtLogin, value);
    }

    public bool MinimizeToMenuBar
    {
        get => _minimizeToMenuBar;
        set => Set(ref _minimizeToMenuBar, value);
    }

    public bool ShowMenuBarExtra
    {
        get => _showMenuBarExtra;
        set => Set(ref _showMenuBarExtra, value);
    }

    /// <summary>Held as text so a half-typed number is shown back to the user, not silently replaced.</summary>
    public string RefreshIntervalText
    {
        get => _refreshIntervalText;
        set
        {
            if (!Set(ref _refreshIntervalText, value)) return;
            Validate();
        }
    }

    public string RefreshIntervalError
    {
        get => _refreshIntervalError;
        private set
        {
            if (Set(ref _refreshIntervalError, value)) Raise(nameof(HasIntervalError));
        }
    }

    public bool HasIntervalError => _refreshIntervalError.Length > 0;

    public double WidgetOpacity
    {
        get => _widgetOpacity;
        set
        {
            if (Set(ref _widgetOpacity, value)) Raise(nameof(OpacityPercent));
        }
    }

    public int OpacityPercent => (int)Math.Round(_widgetOpacity * 100);

    public bool WidgetAlwaysOnTop
    {
        get => _widgetAlwaysOnTop;
        set => Set(ref _widgetAlwaysOnTop, value);
    }

    public bool WidgetRotationEnabled
    {
        get => _widgetRotationEnabled;
        set => Set(ref _widgetRotationEnabled, value);
    }

    public int ChartRangeDays
    {
        get => _chartRangeDays;
        set => Set(ref _chartRangeDays, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set => Set(ref _isSaving, value);
    }

    public async Task SaveAsync()
    {
        Validate();
        if (HasIntervalError || HasBudgetError) return;

        IsSaving = true;
        try
        {
            var interval = int.Parse(_refreshIntervalText.Trim(), CultureInfo.InvariantCulture);
            var providers = new Dictionary<string, ProviderPreference>(
                _original.Providers, StringComparer.OrdinalIgnoreCase);
            foreach (var toggle in Providers)
            {
                var extra = providers.TryGetValue(toggle.ProviderId, out var existing) ? existing.Extra : "";
                providers[toggle.ProviderId] = new ProviderPreference(toggle.Enabled, extra);
            }

            await _session.UpdateAsync(settings => settings with
            {
                Theme = _theme,
                StartWithWindows = _startAtLogin,
                MinimizeToTray = _minimizeToMenuBar,
                ShowTrayIcon = _showMenuBarExtra,
                RefreshIntervalSeconds = interval,
                WidgetOpacity = _widgetOpacity,
                WidgetAlwaysOnTop = _widgetAlwaysOnTop,
                WidgetRotationEnabled = _widgetRotationEnabled,
                ChartRangeDays = _chartRangeDays,
                Providers = providers,
                AgentBudgets = BuildBudgets(),
            }).ConfigureAwait(true);

            _original = _session.Current;
            RequestClose?.Invoke(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveFailed?.Invoke($"Could not save your settings: {exception.Message}");
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Puts every field back to what it was when the page opened, then closes it.</summary>
    public void Cancel()
    {
        Theme = _original.Theme;
        StartAtLogin = _original.StartWithWindows;
        MinimizeToMenuBar = _original.MinimizeToTray;
        ShowMenuBarExtra = _original.ShowTrayIcon;
        RefreshIntervalText = _original.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        WidgetOpacity = _original.WidgetOpacity;
        WidgetAlwaysOnTop = _original.WidgetAlwaysOnTop;
        WidgetRotationEnabled = _original.WidgetRotationEnabled;
        ChartRangeDays = _original.ChartRangeDays;

        foreach (var toggle in Providers)
        {
            toggle.Enabled = !_original.Providers.TryGetValue(toggle.ProviderId, out var preference)
                || preference.Enabled;
        }

        foreach (var row in BudgetRows)
        {
            row.Text = BudgetText(row.Agent);
        }

        RequestClose?.Invoke(false);
    }

    private List<AgentBudgetRowViewModel> CreateBudgetRows(IReadOnlyList<string>? knownAgents)
    {
        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "opencode" };
        foreach (var agent in _original.AgentBudgets.Keys.Concat(knownAgents ?? []))
        {
            var normalised = agent?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalised)) agents.Add(normalised);
        }

        return [.. agents
            .Select(agent => (Agent: agent, DisplayName: AgentUsagePresenter.AgentDisplayName(agent)))
            .OrderBy(entry => string.Equals(entry.Agent, "opencode", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new AgentBudgetRowViewModel(
                entry.Agent, entry.DisplayName, BudgetText(entry.Agent), () => HasBudgetError = BudgetRows.Any(row => row.HasError)))];
    }

    private string BudgetText(string agent) =>
        _original.AgentBudgets.TryGetValue(agent, out var value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty;

    private Dictionary<string, decimal> BuildBudgets()
    {
        var budgets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in BudgetRows)
        {
            if (row.TryGetValue(out var value))
            {
                budgets[row.Agent] = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            }
        }

        return budgets;
    }

    private void Validate()
    {
        var text = _refreshIntervalText.Trim();
        RefreshIntervalError = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds switch
            {
                < 0 => "Enter 0 or more seconds.",
                > 0 and < 30 => "Refreshing more often than every 30 seconds will get the services to rate-limit you.",
                > 86400 => "Enter a day or less, in seconds.",
                _ => string.Empty,
            }
            : "Enter the number of seconds between refreshes, or 0 to refresh only when asked.";

        (SaveCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }
}
