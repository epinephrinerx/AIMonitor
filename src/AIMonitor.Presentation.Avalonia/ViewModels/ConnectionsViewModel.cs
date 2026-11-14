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
    private string _stateWord = "Checking...";
    private string _stateGlyph = "–";
    private ConnectionTone _tone = ConnectionTone.Muted;
    private string _sourceLine;
    private string _foundLine = string.Empty;
    private string _alsoPresent = string.Empty;
    private string _savedKeyMask = string.Empty;
    private bool _isEditing;
    private bool _isKeyRevealed;

    public ConnectionCardViewModel(ProviderDescription description)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        _setupHint = description.SetupHint;
        _sourceLine = description.Tagline;
        ToggleEditCommand = new RelayCommand(() => IsEditing = !IsEditing);
        RedetectCommand = new RelayCommand(() => RedetectRequested?.Invoke(ProviderId));
        ToggleRevealCommand = new RelayCommand(() => IsKeyRevealed = !IsKeyRevealed);
    }

    /// <summary>"Re-detect" on this card: check this service again, and only this one.</summary>
    public event Action<string>? RedetectRequested;

    public ICommand ToggleEditCommand { get; }

    public ICommand RedetectCommand { get; }

    public ICommand ToggleRevealCommand { get; }

    /// <summary>Connected, Limited, Expired, Not connected — a word, not just a colour (PAR-002).</summary>
    public string StateWord
    {
        get => _stateWord;
        private set => Set(ref _stateWord, value);
    }

    public string StateGlyph
    {
        get => _stateGlyph;
        private set => Set(ref _stateGlyph, value);
    }

    public string StateChip => $"{_stateGlyph}  {_stateWord}";

    /// <summary>Which colour the state chip takes: green, yellow, orange or muted, as on Windows.</summary>
    public ConnectionTone Tone
    {
        get => _tone;
        private set
        {
            if (!Set(ref _tone, value)) return;
            Raise(nameof(IsGood));
            Raise(nameof(IsWarning));
            Raise(nameof(IsSerious));
            Raise(nameof(IsMuted));
        }
    }

    public bool IsGood => _tone == ConnectionTone.Good;

    public bool IsWarning => _tone == ConnectionTone.Warning;

    public bool IsSerious => _tone == ConnectionTone.Serious;

    public bool IsMuted => _tone == ConnectionTone.Muted;

    /// <summary>"Detected from Claude Code login (Keychain)  -  hint", or the tagline when nothing was found.</summary>
    public string SourceLine
    {
        get => _sourceLine;
        private set => Set(ref _sourceLine, value);
    }

    /// <summary>"✓  Claude Code login — someone@example.com  ·  Connected", or that nothing was found.</summary>
    public string FoundLine
    {
        get => _foundLine;
        private set
        {
            if (Set(ref _foundLine, value)) Raise(nameof(HasFoundLine));
        }
    }

    public bool HasFoundLine => _foundLine.Length > 0;

    /// <summary>Other sign-ins found on this machine that were not the one used.</summary>
    public string AlsoPresent
    {
        get => _alsoPresent;
        private set
        {
            if (Set(ref _alsoPresent, value)) Raise(nameof(HasAlsoPresent));
        }
    }

    public bool HasAlsoPresent => _alsoPresent.Length > 0;

    /// <summary>"Connect…", "Change…" or "Sign-in help", as the Windows card's button reads.</summary>
    public string ConnectButtonText => !NeedsKey
        ? "Sign-in help"
        : _tone == ConnectionTone.Good && StateWord == "Connected" ? "Change…" : "Connect…";

    /// <summary>The key editor (or, without a key, the sign-in help) is open on this card.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (!Set(ref _isEditing, value)) return;
            Raise(nameof(ShowKeyEditor));
            Raise(nameof(ShowSignInHelp));
            if (!value)
            {
                Key = string.Empty;
                IsKeyRevealed = false;
            }
        }
    }

    public bool ShowKeyEditor => _isEditing && NeedsKey;

    public bool ShowSignInHelp => _isEditing || !NeedsKey;

    /// <summary>Shows the typed key in clear, for checking a paste. Off again when the editor closes.</summary>
    public bool IsKeyRevealed
    {
        get => _isKeyRevealed;
        set
        {
            if (!Set(ref _isKeyRevealed, value)) return;
            Raise(nameof(RevealButtonText));
        }
    }

    public string RevealButtonText => _isKeyRevealed ? "Hide" : "Show";

    /// <summary>
    /// The saved key, masked ("sk-ant-a…wxyz"), so the user can tell a key is there without it being
    /// shown. Empty when none is saved.
    /// </summary>
    public string SavedKeyMask
    {
        get => _savedKeyMask;
        private set
        {
            if (!Set(ref _savedKeyMask, value)) return;
            Raise(nameof(HasSavedKey));
            Raise(nameof(KeyFieldPlaceholder));
        }
    }

    public bool HasSavedKey => _savedKeyMask.Length > 0;

    /// <summary>The key field's grey text: the masked saved key when there is one, the example otherwise.</summary>
    public string KeyFieldPlaceholder => HasSavedKey ? $"Saved: {_savedKeyMask}" : KeyPlaceholder;

    public void SetSavedKey(string? key) => SavedKeyMask = Mask(key);

    /// <summary>
    /// A safe stand-in for a secret: empty for none, bullets for a short one, otherwise the first
    /// eight characters, an ellipsis and the last four (the Windows connect dialog's rule).
    /// </summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        if (secret.Length <= 12) return new string('•', secret.Length);
        return $"{secret[..8]}…{secret[^4..]}";
    }

    /// <summary>The state chip, source line and "found on this machine" lines from what detection reported.</summary>
    private void ApplyDetection(DetectionInfo? detection, bool adminOnly)
    {
        if (detection is null)
        {
            StateWord = "Checking...";
            StateGlyph = "–";
            Tone = ConnectionTone.Muted;
            SourceLine = Tagline;
            FoundLine = string.Empty;
            AlsoPresent = string.Empty;
            RaiseChip();
            return;
        }

        (StateWord, StateGlyph, Tone) = detection.State switch
        {
            DetectionState.Connected => ("Connected", "✓", ConnectionTone.Good),
            DetectionState.Limited => ("Limited", "!", ConnectionTone.Warning),
            DetectionState.Expired => ("Expired", "!", ConnectionTone.Serious),
            _ => ("Not connected", "–", ConnectionTone.Muted),
        };

        if (ProviderId == "claude" && adminOnly)
        {
            // R-019 E14: the Admin key works but Claude Code is not signed in; Expired stays truthful.
            if (detection.State != DetectionState.Expired)
            {
                (StateWord, StateGlyph, Tone) = ("Limited", "!", ConnectionTone.Warning);
            }

            SourceLine = "Admin key connected · Claude Code not signed in";
        }
        else
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrEmpty(detection.SourceLabel)) parts.Add($"Detected from {detection.SourceLabel}");
            if (!string.IsNullOrEmpty(detection.Hint)) parts.Add(detection.Hint);
            SourceLine = parts.Count > 0 ? string.Join("  -  ", parts) : Tagline;
        }

        if (detection.State == DetectionState.NotConnected)
        {
            FoundLine = "No existing sign-in found for this service.";
            AlsoPresent = string.Empty;
        }
        else
        {
            var who = string.IsNullOrEmpty(detection.Account) ? string.Empty : $" — {detection.Account}";
            FoundLine = $"{StateGlyph}  {detection.SourceLabel}{who}  ·  {StateWord}";
            var others = detection.Candidates
                .Where(candidate => !string.Equals(candidate.SourceId, detection.SourceId, StringComparison.Ordinal))
                .Select(candidate => candidate.SourceLabel)
                .Where(label => !string.IsNullOrEmpty(label))
                .ToList();
            AlsoPresent = others.Count > 0 ? "Also present: " + string.Join(", ", others) : string.Empty;
        }

        RaiseChip();
    }

    private void RaiseChip()
    {
        Raise(nameof(StateChip));
        Raise(nameof(ConnectButtonText));
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
            ApplyDetection(null, adminOnly: false);
            StateWord = "Not monitored";
            RaiseChip();
            return;
        }

        ApplyDetection(snapshot.Detection, snapshot.AdminOnly);
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
            ApplyDetection(null, adminOnly: false);
            return;
        }

        IsConnected = status.Installed && status.Error.Length == 0 && status.SignIns.Count > 0;
        (StateWord, StateGlyph, Tone) = IsConnected
            ? ("Connected", "✓", ConnectionTone.Good)
            : status.Installed && status.Error.Length > 0
                ? ("Unavailable", "!", ConnectionTone.Muted)
                : ("Not connected", "–", ConnectionTone.Muted);
        SourceLine = status.Source.Length > 0 ? $"Detected from {status.Source}" : Tagline;
        RaiseChip();
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

/// <summary>The colour family of a connection state chip.</summary>
public enum ConnectionTone
{
    Muted,
    Good,
    Warning,
    Serious,
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
        foreach (var card in Cards)
        {
            card.RedetectRequested += providerId => RedetectRequested?.Invoke(providerId);
        }
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

    /// <summary>
    /// Reads which services have a saved key, so each card can show it masked. The key itself never
    /// reaches the screen.
    /// </summary>
    public async Task LoadSavedKeysAsync()
    {
        foreach (var card in Cards.Where(card => card.NeedsKey))
        {
            try
            {
                var connection = await _connections.GetAsync(card.ProviderId).ConfigureAwait(true);
                card.SetSavedKey(connection.Key);
            }
            catch (Exception exception) when (exception
                is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                card.SetSavedKey(null);
            }
        }
    }

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
            if (clear) card.SetSavedKey(null);
            else if (key.Length > 0) card.SetSavedKey(key);
            card.IsEditing = false;
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
