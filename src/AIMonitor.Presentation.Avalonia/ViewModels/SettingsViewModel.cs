using System.Globalization;
using System.Windows.Input;
using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>One service's "monitor this" switch on the settings page, with what the service offers under it.</summary>
public sealed class ProviderToggle(string providerId, string displayName, bool enabled, string hint = "") : ObservableObject
{
    private bool _enabled = enabled;

    public string ProviderId { get; } = providerId;

    public string DisplayName { get; } = displayName;

    public string Hint { get; } = hint;

    public bool HasHint => Hint.Length > 0;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }
}

/// <summary>
/// The settings page.
///
/// Every field edits a copy, Save writes that copy, and Cancel throws it away. The appearance
/// fields (theme, widget opacity and always-on-top, window size, budgets) are also previewed live
/// through <c>applyPreview</c> while the page is open, as on Windows (PAR-027); Cancel previews the
/// settings the page opened with, which puts the screen back. Nothing reaches the settings file
/// before Save.
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
    private WindowSizeOption _selectedWindowSize;
    private bool _isSaving;
    private bool _hasBudgetError;
    private string _saveError = string.Empty;
    private readonly Action<AppSettings>? _applyPreview;
    private bool _finished;

    /// <param name="knownAgents">Agents ccusage has reported, so each gets a budget row; OpenCode always does.</param>
    /// <param name="applyPreview">Shows appearance settings on screen without saving them.</param>
    public SettingsViewModel(
        SettingsSession session,
        IReadOnlyList<string>? knownAgents = null,
        Action<AppSettings>? applyPreview = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _original = session.Current;
        _applyPreview = applyPreview;

        Providers = [.. ProviderCatalog.All.Select(provider => new ProviderToggle(
            provider.Id,
            provider.DisplayName,
            _original.Providers.TryGetValue(provider.Id, out var preference) ? preference.Enabled : true,
            provider.Tagline.Length > 0 ? provider.Tagline : "Sign-in is managed on the Connections page."))];

        _theme = _original.Theme;
        _startAtLogin = _original.StartWithWindows;
        _minimizeToMenuBar = _original.MinimizeToTray;
        _showMenuBarExtra = _original.ShowTrayIcon;
        _refreshIntervalText = _original.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        _widgetOpacity = Math.Clamp(_original.WidgetOpacity, MinimumOpacity, 1.0);
        _widgetAlwaysOnTop = _original.WidgetAlwaysOnTop;
        _widgetRotationEnabled = _original.WidgetRotationEnabled;
        _selectedWindowSize = WindowSizeOption.FromSize(_original.DashboardWidth, _original.DashboardHeight);

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

    /// <summary>The lowest widget opacity, the floor the settings file holds it to as well.</summary>
    public const double MinimumOpacity = 0.25;

    public IReadOnlyList<DashboardOption<string>> ThemeOptions { get; } =
    [
        new("Follow macOS · default", "system"),
        new("Light", "light"),
        new("Dark", "dark"),
    ];

    public string Theme
    {
        get => _theme;
        set
        {
            if (!Set(ref _theme, value)) return;
            Raise(nameof(SelectedThemeOption));
            ApplyPreview();
        }
    }

    public DashboardOption<string> SelectedThemeOption
    {
        get => DashboardOptions.Find(ThemeOptions, _theme, 0);
        set
        {
            if (value is not null) Theme = value.Value;
        }
    }

    public IReadOnlyList<WindowSizeOption> WindowSizeOptions => WindowSizeOption.All;

    /// <summary>A size picked here is applied to the dashboard straight away; "Remember last size"
    /// keeps whatever it was dragged to.</summary>
    public WindowSizeOption SelectedWindowSize
    {
        get => _selectedWindowSize;
        set
        {
            if (Set(ref _selectedWindowSize, value ?? WindowSizeOption.Standard)) ApplyPreview();
        }
    }

    /// <summary>Why the last Save did not go through, shown on the page.</summary>
    public string SaveError
    {
        get => _saveError;
        private set
        {
            if (Set(ref _saveError, value)) Raise(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => _saveError.Length > 0;

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
            if (!Set(ref _widgetOpacity, Math.Clamp(value, MinimumOpacity, 1.0))) return;
            Raise(nameof(OpacityPercent));
            ApplyPreview();
        }
    }

    public int OpacityPercent => (int)Math.Round(_widgetOpacity * 100);

    public bool WidgetAlwaysOnTop
    {
        get => _widgetAlwaysOnTop;
        set
        {
            if (Set(ref _widgetAlwaysOnTop, value)) ApplyPreview();
        }
    }

    public bool WidgetRotationEnabled
    {
        get => _widgetRotationEnabled;
        set => Set(ref _widgetRotationEnabled, value);
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
        SaveError = string.Empty;
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
                DashboardWidth = _selectedWindowSize.Width,
                DashboardHeight = _selectedWindowSize.Height,
                Providers = providers,
                AgentBudgets = BuildBudgets(),
            }).ConfigureAwait(true);

            _original = _session.Current;
            _finished = true;
            RequestClose?.Invoke(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveError = $"Could not save your settings: {exception.Message}";
            SaveFailed?.Invoke(SaveError);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Puts every field back to what it was when the page opened, then closes it.</summary>
    public void Cancel()
    {
        Discard();
        RequestClose?.Invoke(false);
    }

    /// <summary>
    /// Undoes the preview: also what closing the window any other way than Save does. Does nothing
    /// after a Save, or while one is being written.
    /// </summary>
    public void Discard()
    {
        if (_finished || IsSaving) return;
        _finished = true;

        Theme = _original.Theme;
        StartAtLogin = _original.StartWithWindows;
        MinimizeToMenuBar = _original.MinimizeToTray;
        ShowMenuBarExtra = _original.ShowTrayIcon;
        RefreshIntervalText = _original.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        WidgetOpacity = _original.WidgetOpacity;
        WidgetAlwaysOnTop = _original.WidgetAlwaysOnTop;
        WidgetRotationEnabled = _original.WidgetRotationEnabled;
        SelectedWindowSize = WindowSizeOption.FromSize(_original.DashboardWidth, _original.DashboardHeight);

        foreach (var toggle in Providers)
        {
            toggle.Enabled = !_original.Providers.TryGetValue(toggle.ProviderId, out var preference)
                || preference.Enabled;
        }

        foreach (var row in BudgetRows)
        {
            row.Text = BudgetText(row.Agent);
        }

        _applyPreview?.Invoke(_session.Current with
        {
            Theme = _original.Theme,
            WidgetOpacity = _original.WidgetOpacity,
            WidgetAlwaysOnTop = _original.WidgetAlwaysOnTop,
            DashboardWidth = _original.DashboardWidth,
            DashboardHeight = _original.DashboardHeight,
            AgentBudgets = _original.AgentBudgets,
        });
    }

    private void ApplyPreview()
    {
        // Constructor assignments go straight to the fields, so this only runs for real edits;
        // after Save or Cancel the screen already shows what it should.
        if (_finished || _applyPreview is null || BudgetRows is null) return;

        _applyPreview(_session.Current with
        {
            Theme = _theme,
            WidgetOpacity = _widgetOpacity,
            WidgetAlwaysOnTop = _widgetAlwaysOnTop,
            DashboardWidth = _selectedWindowSize.Width,
            DashboardHeight = _selectedWindowSize.Height,
            AgentBudgets = BuildBudgets(),
        });
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
                entry.Agent, entry.DisplayName, BudgetText(entry.Agent), OnBudgetEdited))];
    }

    private void OnBudgetEdited()
    {
        HasBudgetError = BudgetRows.Any(row => row.HasError);
        if (!HasBudgetError) ApplyPreview();
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
