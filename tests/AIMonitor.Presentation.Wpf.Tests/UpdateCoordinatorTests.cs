using AIMonitor.Application.Version;
using AIMonitor.Presentation.Wpf.Updates;

namespace AIMonitor.Presentation.Wpf.Tests;

public class UpdateCoordinatorTests
{
    private static readonly ReleaseInfo Newer =
        new("v2.1.0", "v2.1.0", "https://github.com/x", "2026-10-20", true, "Setup.exe", "https://github.com/x/Setup.exe", 10);

    private static readonly ReleaseInfo Same = new("v2.0.0", "v2.0.0", "https://github.com/x", "", false);

    private sealed class Harness
    {
        public ReleaseInfo? Release = Newer;
        public Exception? CheckFails;
        public Exception? DownloadFails;
        public bool Agree = true;
        public bool LaunchOk = true;
        public readonly List<string> Messages = [];
        public int Confirms, Downloads, Launches, Quits, ProgressShown;

        public UpdateCoordinator Build() => new(
            new Checker(this), new Downloader(this), new Ui(this), "2.0.0",
            path => { Launches++; return LaunchOk; },
            () => Quits++);

        private sealed class Checker(Harness h) : IVersionChecker
        {
            public Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default) =>
                h.CheckFails is null ? Task.FromResult(h.Release!) : Task.FromException<ReleaseInfo>(h.CheckFails);
        }

        private sealed class Downloader(Harness h) : IUpdateDownloader
        {
            public Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken = default)
            {
                h.Downloads++;
                return h.DownloadFails is null ? Task.FromResult(@"C:\temp\Setup.exe") : Task.FromException<string>(h.DownloadFails);
            }
        }

        private sealed class Ui(Harness h) : IUpdateUi
        {
            public bool ConfirmInstall(ReleaseInfo release, string installedVersion)
            {
                h.Confirms++;
                return h.Agree;
            }

            public void Notify(string message, bool isError) => h.Messages.Add(message);

            public IUpdateProgress ShowProgress(ReleaseInfo release, CancellationTokenSource cancel)
            {
                h.ProgressShown++;
                return new NoProgress();
            }
        }

        private sealed class NoProgress : IUpdateProgress
        {
            public void Report(double value)
            {
            }

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task Newer_AsksThenDownloadsLaunchesAndQuits()
    {
        var h = new Harness();
        await h.Build().CheckAsync(interactive: false);

        Assert.Equal((1, 1, 1, 1), (h.Confirms, h.Downloads, h.Launches, h.Quits));
        Assert.Equal(1, h.ProgressShown);
    }

    [Fact]
    public async Task Declined_DownloadsNothing()
    {
        var h = new Harness { Agree = false };
        await h.Build().CheckAsync(interactive: false);

        Assert.Equal((1, 0, 0, 0), (h.Confirms, h.Downloads, h.Launches, h.Quits));
    }

    [Fact]
    public async Task LaunchCheck_WhenCurrentOrOffline_StaysQuiet()
    {
        var current = new Harness { Release = Same };
        await current.Build().CheckAsync(interactive: false);
        var offline = new Harness { CheckFails = new UpdateCheckException("Could not reach GitHub") };
        await offline.Build().CheckAsync(interactive: false);

        Assert.Empty(current.Messages);
        Assert.Empty(offline.Messages);
        Assert.Equal(0, current.Confirms + offline.Confirms);
    }

    [Fact]
    public async Task MenuCheck_AlwaysAnswers()
    {
        var current = new Harness { Release = Same };
        await current.Build().CheckAsync(interactive: true);
        var offline = new Harness { CheckFails = new UpdateCheckException("Could not reach GitHub") };
        await offline.Build().CheckAsync(interactive: true);

        Assert.Contains("up to date", Assert.Single(current.Messages));
        Assert.Equal("Could not reach GitHub", Assert.Single(offline.Messages));
    }

    [Fact]
    public async Task DownloadFailure_IsReportedAndTheAppStaysOpen()
    {
        var h = new Harness { DownloadFails = new UpdateCheckException("The download is incomplete") };
        await h.Build().CheckAsync(interactive: false);

        Assert.Equal("The download is incomplete", Assert.Single(h.Messages));
        Assert.Equal((0, 0), (h.Launches, h.Quits));
    }

    [Fact]
    public async Task InstallerThatDoesNotStart_KeepsTheAppOpenAndSaysWhere()
    {
        var h = new Harness { LaunchOk = false };
        await h.Build().CheckAsync(interactive: false);

        Assert.Contains(@"C:\temp\Setup.exe", Assert.Single(h.Messages));
        Assert.Equal(0, h.Quits);
    }

    [Fact]
    public async Task ReleaseWithoutInstaller_IsExplained()
    {
        var h = new Harness { Release = Newer with { AssetUrl = "", AssetName = "" } };
        await h.Build().CheckAsync(interactive: false);

        Assert.Contains("no installer", Assert.Single(h.Messages));
        Assert.Equal(0, h.Downloads);
    }
}
