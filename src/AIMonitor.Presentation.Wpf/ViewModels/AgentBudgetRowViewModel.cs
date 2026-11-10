using System.Globalization;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// One editable monthly-budget row in the Settings dialog.
/// </summary>
public sealed class AgentBudgetRowViewModel : ViewModelBase
{
    private string _text;
    private string _error = "";
    private readonly Action? _onChanged;

    public AgentBudgetRowViewModel(string agent, string displayName, string? initialText, Action? onChanged = null)
    {
        Agent = agent;
        DisplayName = displayName;
        _text = initialText ?? "";
        _onChanged = onChanged;
        Validate();
    }

    /// <summary>
    /// Normalised lower-case agent name (used as the settings key).
    /// </summary>
    public string Agent { get; }

    /// <summary>
    /// User-facing label for the row.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Current contents of the budget text box. Empty means "no budget".
    /// </summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? ""))
            {
                Validate();
                _onChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Validation message for this row, or empty when the row is valid/empty.
    /// </summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>
    /// True when <see cref="Error"/> is non-empty.
    /// </summary>
    public bool HasError => _error.Length > 0;

    /// <summary>
    /// Parses the row when it is valid and non-empty.
    /// </summary>
    public bool TryGetValue(out decimal value)
    {
        value = 0m;
        var error = ValidateBudget(_text, out var parsed);
        if (error is not null || parsed is null)
        {
            return false;
        }

        value = parsed.Value;
        return true;
    }

    private void Validate()
    {
        Error = ValidateBudget(_text, out _) ?? "";
    }

    /// <summary>
    /// Validates a budget string. Returns <c>null</c> for empty or valid values;
    /// otherwise the standard error message.
    /// </summary>
    public static string? ValidateBudget(string? text, out decimal? parsed)
    {
        parsed = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(1).Trim();
        }

        if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return "Enter an amount in USD between 0.01 and 1,000,000, or leave it empty.";
        }

        if (value <= 0m || value > 1_000_000m)
        {
            return "Enter an amount in USD between 0.01 and 1,000,000, or leave it empty.";
        }

        parsed = value;
        return null;
    }
}
