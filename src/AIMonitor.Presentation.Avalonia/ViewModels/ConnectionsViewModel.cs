using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>One service's card: how it is signed in, and what to do about it.</summary>
public sealed class ConnectionCardViewModel : ObservableObject
{
    private string _status = "Not checked yet";
    private string _account = string.Empty;
    private string _setupHint;
    private bool _isConnected;
    private string _key = string.Empty;
    private string _extra = string.Empty;

    public ConnectionCardViewModel(ProviderDescription description)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        _setupHint = description.SetupHint;
    }

    public ProviderDescription Description { get; }

    public string ProviderId => Description.Id;

    public string DisplayName => Description.DisplayName;

    public string Tagline => Description.Tagline;

    public bool NeedsKey => Description.NeedsKey;

    public string KeyLabel => Description.KeyLabel;

    public string KeyPlaceholder => Description.KeyPlaceholder;

    public string ExtraLabel => Description.ExtraLabel;

    public string ExtraPlaceholder => Description.ExtraPlaceholder;

    public bool HasExtra => Description.ExtraLabel.Length > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Account
    {
        get => _account;
        private set
        {
            if (Set(ref _account, value)) Raise(nameof(HasAccount));
        }
    }

    public bool HasAccount => _account.Length > 0;

    public string SetupHint
    {
        get => _setupHint;
        private set => Set(ref _setupHint, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    /// <summary>
    /// The key as typed. Only ever written, never filled in from what is stored: the stored secret
    /// is the user's, and putting it back on screen gives any passer-by a free look at it.
    /// </summary>
    public string Key
    {
        get => _key;
        set => Set(ref _key, value);
    }

    public string Extra
    {
        get => _extra;
        set => Set(ref _extra, value);
    }

    public void Apply(ProviderSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            Status = "Not monitored";
            IsConnected = false;
            return;
        }

        IsConnected = snapshot.Ok;
        Account = snapshot.Account;
        SetupHint = snapshot.SetupHint.Length > 0 ? snapshot.SetupHint : Description.SetupHint;
        Status = snapshot switch
        {
            { Error: { Length: > 0 } error } => error,
            { Unauthorized: true } => "Signed in, but this account is not allowed to read usage",
            { Configured: true } => "Connected",
            _ => "Not signed in",
        };
    }
}

/// <summary>The connections page: who is signed in, and the one place to enter a key.</summary>
public sealed class ConnectionsViewModel : ObservableObject
{
    private readonly SettingsSession _session;
    private readonly ProviderConnectionStore _connections;
    private bool _showAtStartup;
    private string _message = string.Empty;

    public ConnectionsViewModel(SettingsSession session, ProviderConnectionStore connections)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _showAtStartup = session.Current.ShowConnectionsAtStartup;

        Cards = [.. ProviderCatalog.All.Select(provider => new ConnectionCardViewModel(provider))];
        RedetectAllCommand = new RelayCommand(() => RedetectRequested?.Invoke(null));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public event Action<string?>? RedetectRequested;

    public event Action? CloseRequested;

    public ObservableCollection<ConnectionCardViewModel> Cards { get; }

    public ICommand RedetectAllCommand { get; }

    public ICommand CloseCommand { get; }

    public bool ShowAtStartup
    {
        get => _showAtStartup;
        set
        {
            if (!Set(ref _showAtStartup, value)) return;
            _ = _session.UpdateAsync(settings => settings with { ShowConnectionsAtStartup = value });
        }
    }

    /// <summary>What just happened, for the line under the cards.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value)) Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    public void Update(IReadOnlyDictionary<string, ProviderSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        foreach (var card in Cards)
        {
            card.Apply(snapshots.GetValueOrDefault(card.ProviderId));
        }
    }

    /// <summary>
    /// Stores what the user typed for one service, then clears the field.
    ///
    /// An empty field leaves the stored key alone rather than wiping it: clearing is its own action
    /// below, so nobody loses a key by tabbing through the page.
    /// </summary>
    public Task SaveAsync(ConnectionCardViewModel card) => WriteAsync(card, clear: false);

    /// <summary>
    /// Removes the stored key for one service.
    ///
    /// On macOS this has to reach the Keychain, not just the settings file — the 1.x port found that
    /// clearing only the setting left the real secret sitting in the Keychain.
    /// </summary>
    public Task ClearAsync(ConnectionCardViewModel card) => WriteAsync(card, clear: true);

    private async Task WriteAsync(ConnectionCardViewModel card, bool clear)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.NeedsKey)
        {
            Message = $"{card.DisplayName} is signed in on this machine, not with a key.";
            return;
        }

        var extra = card.Extra.Trim();
        if (extra.Length > 0)
        {
            if (!double.TryParse(extra, NumberStyles.Float, CultureInfo.InvariantCulture, out var budget))
            {
                Message = $"{card.ExtraLabel} needs to be a number.";
                return;
            }

            if (budget < 0)
            {
                Message = $"{card.ExtraLabel} cannot be negative.";
                return;
            }
        }

        var key = clear ? string.Empty : card.Key.Trim();
        try
        {
            await _connections.SaveAsync(card.ProviderId, key, clear, extra).ConfigureAwait(true);
            card.Key = string.Empty;
            Message = clear
                ? $"Cleared the saved key for {card.DisplayName}."
                : key.Length == 0
                    ? $"Left the saved key for {card.DisplayName} as it was."
                    : $"Saved the key for {card.DisplayName}.";
            RedetectRequested?.Invoke(card.ProviderId);
        }
        catch (Exception exception) when (exception
            is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Message = $"Could not save the key for {card.DisplayName}: {exception.Message}";
        }
    }
}
