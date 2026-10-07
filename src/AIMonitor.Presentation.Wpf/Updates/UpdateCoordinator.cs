using AIMonitor.Application.Version;

namespace AIMonitor.Presentation.Wpf.Updates;

public interface IUpdateProgress : IProgress<double>, IDisposable
{
}

/// <summary>Everything the update flow needs from the screen, so the flow itself can be tested.</summary>
public interface IUpdateUi
{
    /// <summary>Asks whether to download and install now.</summary>
    bool ConfirmInstall(ReleaseInfo release, string installedVersion);

    void Notify(string message, bool isError);

    IUpdateProgress ShowProgress(ReleaseInfo release, CancellationTokenSource cancel);
}

/// <summary>
/// Check GitHub, ask, download the installer from the release, run it and let the app quit.
/// A launch-time check stays quiet unless there is something to offer; a menu check always answers.
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly IVersionChecker _checker;
    private readonly IUpdateDownloader _downloader;
    private readonly IUpdateUi _ui;
    private readonly Func<string, bool> _launchInstaller;
    private readonly Action _quit;
    private readonly string _installedVersion;
    private int _busy;

    public UpdateCoordinator(
        IVersionChecker checker,
        IUpdateDownloader downloader,
        IUpdateUi ui,
        string installedVersion,
        Func<string, bool> launchInstaller,
        Action quit)
    {
        _checker = checker;
        _downloader = downloader;
        _ui = ui;
        _installedVersion = installedVersion;
        _launchInstaller = launchInstaller;
        _quit = quit;
    }

    public async Task CheckAsync(bool interactive)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            ReleaseInfo release;
            try
            {
                release = await _checker.CheckLatestAsync(_installedVersion).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (interactive)
                {
                    _ui.Notify(ex is UpdateCheckException ? ex.Message : $"Update check failed: {ex.Message}", true);
                }

                return;
            }

            if (!release.IsNewer)
            {
                if (interactive)
                {
                    _ui.Notify($"You are up to date. The newest release is {release.Tag}; you have {_installedVersion}.", false);
                }

                return;
            }

            await OfferAsync(release).ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Offers a release already found (the About page's "Get the update").</summary>
    public async Task OfferAsync(ReleaseInfo release)
    {
        if (!_ui.ConfirmInstall(release, _installedVersion))
        {
            return;
        }

        if (!release.HasInstaller)
        {
            _ui.Notify($"{release.Tag} has no installer attached. Open the releases page to get it.", true);
            return;
        }

        using var cancel = new CancellationTokenSource();
        string path;
        try
        {
            using var progress = _ui.ShowProgress(release, cancel);
            path = await _downloader.DownloadAsync(release, progress, cancel.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _ui.Notify(ex is UpdateCheckException ? ex.Message : $"Download failed: {ex.Message}", true);
            return;
        }

        if (!_launchInstaller(path))
        {
            _ui.Notify($"The installer was downloaded but could not be started. You can run it yourself:\n{path}", true);
            return;
        }

        _quit();
    }
}
