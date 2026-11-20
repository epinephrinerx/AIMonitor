using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.OpenCode;
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

    /// <summary>
    /// What the typed key looks like, before it is saved (R-019): only organization Admin keys can
    /// read usage, and pasting an ordinary key is the usual mistake.
    /// </summary>
    public string KeyHint
    {
        get
        {
            var key = _key.Trim();
            if (key.Length == 0) return string.Empty;

            return ProviderId switch
            {
                "openai" => key.StartsWith("sk-admin-", StringComparison.Ordinal)
                    ? "Admin API key format recognized (sk-admin-…)."
                    : "API spend requires an organization Admin key (sk-admin-…). Project or user keys (e.g. sk-proj-…) cannot read organization usage or costs.",
                "claude" => key.StartsWith("sk-ant-admin", StringComparison.Ordinal)
                    ? "Admin API key format recognized (sk-ant-admin…)."
                    : "API usage and cost require an organization Admin key (sk-ant-admin…). Ordinary or workspace API keys cannot read organization usage.",
                _ => string.Empty,
            };
        }
    }

    public bool HasKeyHint => KeyHint.Length > 0;

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
        set
        {
            if (!Set(ref _key, value)) return;
            Raise(nameof(KeyHint));
            Raise(nameof(HasKeyHint));
        }
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
            // R-019 E14: the Admin key works but Claude Code is not signed in, so there is usage and
            // cost but no quota.
            { AdminOnly: true, Error: null or "" } when ProviderId == "claude" =>
                "Limited · Admin key connected · Claude Code not signed in",
            { Error: { Length: > 0 } error } => error,
            { Unauthorized: true } => "Signed in, but this account is not allowed to read usage",
            { Configured: true } => "Connected",
            _ => "Not signed in",
        };
    }

    /// <summary>OpenCode's card, from what <c>opencode auth list</c> or auth.json says (R-020). Null is "still checking".</summary>
    public void ApplyOpenCode(OpenCodeAuthStatus? status)
    {
        SetupHint = Description.SetupHint;
        if (status is null)
        {
            Status = "Checking...";
            Account = string.Empty;
            IsConnected = false;
            return;
        }

        IsConnected = status.Installed && status.Error.Length == 0 && status.SignIns.Count > 0;
        if (!status.Installed)
        {
            Status = "Not connected · OpenCode not found on PATH";
            Account = string.Empty;
            SetupHint = "Install OpenCode, then run `opencode auth login`.";
        }
        else if (status.Error.Length > 0)
        {
            Status = "Unavailable · could not check";
            Account = string.Empty;
            SetupHint = status.Error;
        }
        else if (status.SignIns.Count == 0)
        {
            Status = "Not connected · no providers signed in";
            Account = string.Empty;
            SetupHint = "Run `opencode auth login` to sign in.";
        }
        else
        {
            Status = status.Source == "auth.json" ? "Connected · detected from auth.json" : "Connected · detected from opencode auth list";
            if (status.MayBeOutdated) Status += " · may be out of date (OpenCode 2.x keeps sign-ins in its database)";
            Account = "Signed in: " + string.Join(", ", status.SignIns.Select(signIn => $"{signIn.Name} ({signIn.Kind})"));
        }
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

        OpenCodeCard = new ConnectionCardViewModel(ProviderCatalog.OpenCode);
        OpenCodeCard.ApplyOpenCode(null);
        Cards = [.. ProviderCatalog.All.Select(provider => new ConnectionCardViewModel(provider)), OpenCodeCard];
        RedetectAllCommand = new RelayCommand(() => RedetectRequested?.Invoke(null));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public event Action<string?>? RedetectRequested;

    public event Action? CloseRequested;

    public ObservableCollection<ConnectionCardViewModel> Cards { get; }

    public ConnectionCardViewModel OpenCodeCard { get; }

    /// <summary>Agents ccusage found that have no tab here, and how each signs in (R-021).</summary>
    public string OtherAgentsStatus { get; private set; } = "Checking ccusage…";

    public IReadOnlyList<OtherAgentRow> OtherAgents { get; private set; } = [];

    public bool HasOtherAgents => OtherAgents.Count > 0;

    public void UpdateOpenCode(OpenCodeAuthStatus? status) => OpenCodeCard.ApplyOpenCode(status);

    public void UpdateOtherAgents(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError)
    {
        var result = OtherAgentsPresenter.Build(lastGood, lastError);
        OtherAgentsStatus = result.Status;
        OtherAgents = result.Rows;
        Raise(nameof(OtherAgentsStatus));
        Raise(nameof(OtherAgents));
        Raise(nameof(HasOtherAgents));
    }

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
            if (ReferenceEquals(card, OpenCodeCard)) continue;
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
