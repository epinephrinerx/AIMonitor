namespace AIMonitor.Presentation.Wpf.ViewModels;

using System.Windows.Input;
using System.Windows.Media;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;

/// <summary>
/// ViewModel representing a single provider's connection status card on the connections page.
/// Status is represented via explicit textual state words and glyphs alongside visual indicators.
/// </summary>
public sealed class ConnectionCardViewModel : ViewModelBase
{
    private DetectionInfo? _detection;
    private string _stateWord = "Checking...";
    private string _stateGlyph = "–";
    private string _account = "Not signed in";
    private string _sourceLine = "";
    private string _connectButtonText = "Connect...";
    private readonly int _seriesIndex;

    public ProviderMeta Meta { get; }

    /// <summary>Round service badge: the service's categorical hue, tinted behind the initial (1.3.3 <c>_Badge</c>).</summary>
    public Brush BadgeBrush => Solid(ThemePalette.For(ThemeManager.Instance.IsDark).Series(_seriesIndex), 0xFF);

    public Brush BadgeTint => Solid(ThemePalette.For(ThemeManager.Instance.IsDark).Series(_seriesIndex), 0x29);

    /// <summary>State chip colour: green connected, yellow limited, orange expired, muted ink when absent.</summary>
    public Brush StateBrush => Solid(StateColour(), 0xFF);

    public Brush StateTint => Solid(StateColour(), 0x24);

    private System.Windows.Media.Color StateColour() => _detection?.State switch
    {
        DetectionState.Connected => ThemePalette.StatusGood,
        DetectionState.Limited => ThemePalette.StatusWarning,
        DetectionState.Expired => ThemePalette.StatusSerious,
        _ => ThemePalette.For(ThemeManager.Instance.IsDark).InkMuted,
    };

    private static SolidColorBrush Solid(System.Windows.Media.Color colour, byte alpha)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    private void RefreshColours()
    {
        OnPropertyChanged(nameof(BadgeBrush));
        OnPropertyChanged(nameof(BadgeTint));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(StateTint));
    }

    public DetectionInfo? Detection
    {
        get => _detection;
        private set => SetProperty(ref _detection, value);
    }

    public string StateWord
    {
        get => _stateWord;
        private set => SetProperty(ref _stateWord, value);
    }

    public string StateGlyph
    {
        get => _stateGlyph;
        private set => SetProperty(ref _stateGlyph, value);
    }

    public string Account
    {
        get => _account;
        private set => SetProperty(ref _account, value);
    }

    public string SourceLine
    {
        get => _sourceLine;
        private set => SetProperty(ref _sourceLine, value);
    }

    public string ConnectButtonText
    {
        get => _connectButtonText;
        private set => SetProperty(ref _connectButtonText, value);
    }

    public ICommand ConnectCommand { get; }
    public ICommand RedetectCommand { get; }

    public event Action<string>? ConnectRequested;
    public event Action<string>? RedetectRequested;

    public ConnectionCardViewModel(ProviderMeta meta)
    {
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _seriesIndex = Math.Max(0, ProviderMeta.All.ToList().FindIndex(m => m.Id == meta.Id));
        ThemeManager.Instance.ThemeChanged += () => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshColours);

        ConnectCommand = new RelayCommand(() => ConnectRequested?.Invoke(Meta.Id));
        RedetectCommand = new RelayCommand(() => RedetectRequested?.Invoke(Meta.Id));

        Update(null);
    }

    /// <summary>
    /// Updates the card representation from detection telemetry.
    /// When <paramref name="detection"/> is null, behaves as NotConnected with "Checking..." StateWord.
    /// </summary>
    public void Update(DetectionInfo? detection)
    {
        Detection = detection;
        RefreshColours();

        if (detection is null)
        {
            StateWord = "Checking...";
            StateGlyph = "–";
            Account = "Not signed in";
            SourceLine = Meta.Tagline;
            ConnectButtonText = !Meta.NeedsKey ? "Sign-in help" : "Connect...";
            return;
        }

        StateWord = detection.State switch
        {
            DetectionState.Connected => "Connected",
            DetectionState.Limited => "Limited",
            DetectionState.Expired => "Expired",
            _ => "Not connected"
        };

        StateGlyph = detection.State switch
        {
            DetectionState.Connected => "✓",
            DetectionState.Limited => "!",
            DetectionState.Expired => "!",
            _ => "–"
        };

        if (!string.IsNullOrEmpty(detection.Account))
        {
            Account = detection.Account;
        }
        else if (detection.State == DetectionState.NotConnected)
        {
            Account = "Not signed in";
        }
        else
        {
            Account = Meta.Tagline;
        }

        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(detection.SourceLabel))
        {
            parts.Add($"Detected from {detection.SourceLabel}");
        }
        if (!string.IsNullOrEmpty(detection.Hint))
        {
            parts.Add(detection.Hint);
        }

        SourceLine = parts.Count > 0 ? string.Join("  -  ", parts) : Meta.Tagline;

        if (!Meta.NeedsKey)
        {
            ConnectButtonText = "Sign-in help";
        }
        else
        {
            ConnectButtonText = detection.State == DetectionState.Connected ? "Change..." : "Connect...";
        }
    }

    /// <summary>
    /// Displays the card in an unavailable state when detection failed and no prior detection was present.
    /// </summary>
    public void SetUnavailable(string? error)
    {
        Detection = null;
        StateWord = "Unavailable";
        StateGlyph = "!";
        Account = "Could not check";
        SourceLine = error ?? "";
        ConnectButtonText = !Meta.NeedsKey ? "Sign-in help" : "Connect...";
    }
}
