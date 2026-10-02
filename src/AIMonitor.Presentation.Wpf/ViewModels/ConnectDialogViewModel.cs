namespace AIMonitor.Presentation.Wpf.ViewModels;

using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using AIMonitor.Application.Providers;
using AIMonitor.Domain;

/// <summary>
/// ViewModel for the Connect dialog used to configure credentials and settings for an AI provider.
/// Follows three-way key semantics (replace, clear, untouched) upon Save without network validation on the UI thread.
/// </summary>
public sealed class ConnectDialogViewModel : ViewModelBase
{
    private readonly ProviderConnectionStore _store;
    private string _key = "";
    private string _extra = "";
    private string _keyPlaceholder = "";
    private bool _clearRequested;
    private bool _isKeyRevealed;

    public ProviderMeta Meta { get; }
    public DetectionInfo? Detection { get; }
    public ProviderConnection Existing { get; }

    public string Title => Meta.NeedsKey ? $"Connect {Meta.DisplayName}" : $"{Meta.DisplayName} sign-in";

    public bool NeedsKey => Meta.NeedsKey;

    public bool CanBrowse => Meta.KeyPlaceholder.Contains("json", StringComparison.OrdinalIgnoreCase);

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public string Extra
    {
        get => _extra;
        set => SetProperty(ref _extra, value);
    }

    public string KeyPlaceholder
    {
        get => _keyPlaceholder;
        private set => SetProperty(ref _keyPlaceholder, value);
    }

    public bool ClearRequested
    {
        get => _clearRequested;
        private set => SetProperty(ref _clearRequested, value);
    }

    public bool IsKeyRevealed
    {
        get => _isKeyRevealed;
        set => SetProperty(ref _isKeyRevealed, value);
    }

    public string FoundLine { get; }

    public string Hint => Detection?.Hint ?? "";

    public string AlsoPresent { get; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand BrowseCommand { get; }

    public event Action<bool>? RequestClose;
    public event Action<string>? SaveFailed;
    public event Action? BrowseRequested;

    public ConnectDialogViewModel(
        ProviderMeta meta,
        DetectionInfo? detection,
        ProviderConnection existing,
        ProviderConnectionStore store)
    {
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        Detection = detection;
        Existing = existing ?? throw new ArgumentNullException(nameof(existing));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _extra = existing.Extra ?? "";
        _keyPlaceholder = !string.IsNullOrEmpty(existing.Key)
            ? Mask(existing.Key)
            : meta.KeyPlaceholder;

        // "Found on this machine" section
        if (detection is null || detection.State == DetectionState.NotConnected)
        {
            FoundLine = "No existing sign-in found for this service.";
            AlsoPresent = "";
        }
        else
        {
            var glyph = detection.State switch
            {
                DetectionState.Connected => "✓",
                DetectionState.Limited => "!",
                DetectionState.Expired => "!",
                _ => "–"
            };

            var stateWord = detection.State switch
            {
                DetectionState.Connected => "Connected",
                DetectionState.Limited => "Limited",
                DetectionState.Expired => "Expired",
                _ => "Not connected"
            };

            var who = !string.IsNullOrEmpty(detection.Account) ? $" — {detection.Account}" : "";
            FoundLine = $"{glyph}  {detection.SourceLabel}{who}  ·  {stateWord}";

            var others = detection.Candidates
                .Where(c => !string.Equals(c.SourceId, detection.SourceId, StringComparison.Ordinal))
                .Select(c => c.SourceLabel)
                .Where(lbl => !string.IsNullOrEmpty(lbl))
                .ToList();

            AlsoPresent = others.Count > 0 ? "Also present: " + string.Join(", ", others) : "";
        }

        SaveCommand = new RelayCommand(async () => await SaveAsync());
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        ClearCommand = new RelayCommand(ExecuteClear);
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke());
    }

    private void ExecuteClear()
    {
        ClearRequested = true;
        Key = string.Empty;
        KeyPlaceholder = "(cleared when you save)";
    }

    public void SetKeyFromBrowse(string path)
    {
        Key = path ?? string.Empty;
        IsKeyRevealed = true;
    }

    public async Task SaveAsync()
    {
        if (!Meta.NeedsKey)
        {
            // NeedsKey=false (Claude): ไม่มี field, ปุ่มเดียว Close; SaveCommand ไม่ทำอะไร
            return;
        }

        try
        {
            var extraToSave = string.IsNullOrEmpty(Meta.ExtraLabel) ? null : Extra;
            await _store.SaveAsync(Meta.Id, Key, ClearRequested, extraToSave).ConfigureAwait(true);
            RequestClose?.Invoke(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            var message = ex switch
            {
                UnauthorizedAccessException => "Failed to save credentials: Access denied.",
                CryptographicException => "Failed to save credentials: Cryptographic error.",
                _ => "Failed to save credentials due to an I/O error."
            };

            SaveFailed?.Invoke(message);
        }
    }

    /// <summary>
    /// Produces a safe masked representation of a secret key.
    /// If empty: returns "". If length &lt;= 12: returns bullets matching length.
    /// Otherwise: returns first 8 characters, an ellipsis (…), and the last 4 characters.
    /// </summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "";
        }

        if (secret.Length <= 12)
        {
            return new string('•', secret.Length);
        }

        return $"{secret[..8]}…{secret[^4..]}";
    }
}
