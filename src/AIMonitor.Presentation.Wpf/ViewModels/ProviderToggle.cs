namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>One "Monitor this service" row of the Settings dialog.</summary>
public sealed class ProviderToggle(string id, string displayName, string hint, bool isEnabled) : ViewModelBase
{
    private bool _isEnabled = isEnabled;

    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    /// <summary>The service's tagline, or where sign-in is managed.</summary>
    public string Hint { get; } = hint;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}
