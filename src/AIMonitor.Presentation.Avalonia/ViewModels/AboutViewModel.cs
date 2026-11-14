using System.Reflection;
using System.Windows.Input;
using AIMonitor.Application.Version;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>About: who made this, what licence it carries, and whether there is a newer build.</summary>
public sealed class AboutViewModel : ObservableObject
{
    private readonly IVersionChecker? _versionChecker;
    private string _updateStatus = string.Empty;
    private bool _isChecking;
    private ReleaseInfo? _latestRelease;

    public AboutViewModel(IVersionChecker? versionChecker = null)
    {
        _versionChecker = versionChecker;
        AppVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            // A "+<commit>" suffix is build metadata, not something to show the user.
            .Split('+')[0]
            ?? "unknown";

        CheckUpdatesCommand = new AsyncCommand(CheckUpdatesAsync, () => !IsChecking && _versionChecker is not null);
        OpenProjectCommand = new RelayCommand(() => OpenUrlRequested?.Invoke(ProjectUrl));
        OpenReleasesCommand = new RelayCommand(() => OpenUrlRequested?.Invoke(_latestRelease?.Url ?? ReleasesUrl));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    /// <summary>
    /// The branch the macOS edition is released from. Releases cut from main are Windows builds,
    /// so the update check looks only at releases whose tag is on this branch.
    /// </summary>
    public const string ReleaseBranch = "Mac";

    public event Action<string>? OpenUrlRequested;

    public event Action? CloseRequested;

    public string AppName => "AI Usage Monitor";

    public string Edition => "macOS Edition";

    public string AppVersion { get; }

    public string DeveloperName => "Apichart Chantanis";

    public string DeveloperEmail => "apichart@apichart.net";

    public string ProjectUrl => "https://github.com/epinephrinerx/AIMonitor";

    public string ReleasesUrl => ProjectUrl + "/releases";

    public string LicenseSummary =>
        "Licensed under the GNU General Public License v3.0. The full licence and the third-party "
        + "notices ship beside the app.";

    public ICommand CheckUpdatesCommand { get; }

    public ICommand OpenProjectCommand { get; }

    public ICommand OpenReleasesCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>The licence text that ships in the bundle, or null when it is missing.</summary>
    public string? LicenseText => ReadBesideApp("LICENSE") ?? ReadBesideApp("LICENSE.txt");

    public string? NoticesText => ReadBesideApp("THIRD-PARTY-NOTICES.md");

    public bool HasLicenseText => LicenseText is not null;

    public bool HasNoticesText => NoticesText is not null;

    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (Set(ref _updateStatus, value)) Raise(nameof(HasUpdateStatus));
        }
    }

    public bool HasUpdateStatus => _updateStatus.Length > 0;

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (!Set(ref _isChecking, value)) return;
            Raise(nameof(CheckButtonText));
            (CheckUpdatesCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string CheckButtonText => _isChecking ? "Checking…" : "Check for updates";

    public ReleaseInfo? LatestRelease
    {
        get => _latestRelease;
        private set
        {
            if (Set(ref _latestRelease, value)) Raise(nameof(ReleaseButtonText));
        }
    }

    public string ReleaseButtonText =>
        _latestRelease?.IsNewer == true ? "Open the new release" : "Open the releases page";

    public async Task CheckUpdatesAsync()
    {
        if (_versionChecker is null)
        {
            UpdateStatus = "Update checking is not available in this build.";
            return;
        }

        IsChecking = true;
        UpdateStatus = string.Empty;
        try
        {
            var release = await _versionChecker.CheckLatestAsync(AppVersion).ConfigureAwait(true);
            LatestRelease = release;
            UpdateStatus = release.IsNewer
                ? $"{release.Name} is available."
                : "This is the newest release.";
        }
        catch (UpdateCheckException exception)
        {
            UpdateStatus = exception.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Reads a file that ships beside the executable. GPL-3.0 requires the licence to travel with
    /// the app, so this is also how the About page proves it did.
    /// </summary>
    private static string? ReadBesideApp(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
