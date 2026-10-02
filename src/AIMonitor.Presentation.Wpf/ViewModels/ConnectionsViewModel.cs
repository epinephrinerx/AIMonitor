namespace AIMonitor.Presentation.Wpf.ViewModels;

using System.Windows.Input;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;

/// <summary>
/// ViewModel for the Connections landing page showing the status of all supported AI provider connections.
/// </summary>
public sealed class ConnectionsViewModel : ViewModelBase
{
    private readonly SettingsSession _settingsSession;
    private bool _showAtStartup;

    public ConnectionCardViewModel ClaudeCard { get; }
    public ConnectionCardViewModel OpenAiCard { get; }
    public ConnectionCardViewModel GeminiCard { get; }
    public IReadOnlyList<ConnectionCardViewModel> Cards { get; }

    /// <summary>
    /// Gets or sets whether the Connections page should be displayed automatically on application startup.
    /// Changes are persisted to <see cref="AppSettings.ShowConnectionsAtStartup"/> via <see cref="SettingsSession.UpdateAsync"/>.
    /// </summary>
    public bool ShowAtStartup
    {
        get => _showAtStartup;
        set
        {
            if (SetProperty(ref _showAtStartup, value))
            {
                LastSaveTask = _settingsSession.UpdateAsync(s => s with { ShowConnectionsAtStartup = value });
            }
        }
    }

    /// <summary>
    /// Tracks the in-flight or last completed persistence task for <see cref="ShowAtStartup"/>.
    /// </summary>
    public Task? LastSaveTask { get; private set; }

    public ICommand RedetectAllCommand { get; }
    public ICommand OpenDashboardCommand { get; }

    public event Action<string>? ConnectRequested;
    public event Action<string?>? RedetectRequested;
    public event Action? OpenDashboardRequested;

    public ConnectionsViewModel(SettingsSession settingsSession)
    {
        _settingsSession = settingsSession ?? throw new ArgumentNullException(nameof(settingsSession));
        _showAtStartup = _settingsSession.Current.ShowConnectionsAtStartup;

        ClaudeCard = new ConnectionCardViewModel(ProviderMeta.Claude);
        OpenAiCard = new ConnectionCardViewModel(ProviderMeta.OpenAi);
        GeminiCard = new ConnectionCardViewModel(ProviderMeta.Gemini);
        Cards = [ClaudeCard, OpenAiCard, GeminiCard];

        foreach (var card in Cards)
        {
            card.ConnectRequested += id => ConnectRequested?.Invoke(id);
            card.RedetectRequested += id => RedetectRequested?.Invoke(id);
        }

        RedetectAllCommand = new RelayCommand(() => RedetectRequested?.Invoke(null));
        OpenDashboardCommand = new RelayCommand(() => OpenDashboardRequested?.Invoke());

        _settingsSession.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_showAtStartup != settings.ShowConnectionsAtStartup)
        {
            _showAtStartup = settings.ShowConnectionsAtStartup;
            OnPropertyChanged(nameof(ShowAtStartup));
        }
    }

    /// <summary>
    /// Updates all provider cards with the latest detection outcome from each provider's snapshot.
    /// </summary>
    public void Update(IReadOnlyDictionary<string, ProviderSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        foreach (var card in Cards)
        {
            if (snapshots.TryGetValue(card.Meta.Id, out var snapshot))
            {
                card.Update(snapshot.Detection);
            }
            else
            {
                card.Update(null);
            }
        }
    }
}
