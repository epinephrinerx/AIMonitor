namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class SupplementaryRowViewModel : ViewModelBase
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Group { get; init; } = "";
}

public sealed class SupplementaryPillViewModel : ViewModelBase
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string DisplayText => string.IsNullOrEmpty(Label) ? Value : $"{Label}: {Value}";
}

public sealed class SupplementaryDetailsViewModel : ViewModelBase
{
    private bool _isResetDetailsExpanded;

    public string ProviderId { get; init; } = "";
    public string SectionTitle { get; init; } = "Supplementary Details & Telemetry";
    public string ResetTitle { get; init; } = "Rate Limit Resets";
    public string ResetBadge { get; init; } = "";
    public string ResetSummary { get; init; } = "";
    public string ResetDetails { get; init; } = "";
    public IReadOnlyList<SupplementaryRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<SupplementaryPillViewModel> ActivityPills { get; init; } = [];
    public string ActivityTitle { get; init; } = "Activity";
    public string ActivityNote { get; init; } = "";

    public bool HasRows => Rows.Count > 0;
    public bool HasActivity => ActivityPills.Count > 0 || !string.IsNullOrEmpty(ActivityNote);

    /// <summary>True when any part of the reset row has content; a ccusage-only section has none (C19).</summary>
    public bool HasResetInfo =>
        !string.IsNullOrEmpty(ResetTitle)
        || !string.IsNullOrEmpty(ResetBadge)
        || !string.IsNullOrEmpty(ResetSummary)
        || !string.IsNullOrEmpty(ResetDetails);

    public bool IsResetDetailsExpanded
    {
        get => _isResetDetailsExpanded;
        set => SetProperty(ref _isResetDetailsExpanded, value);
    }
}
