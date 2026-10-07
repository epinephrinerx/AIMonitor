using AIMonitor.Application.Version;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public class AboutViewModelTests
{
    [Fact]
    public void Constructor_InitializesDeveloperAndMetadata()
    {
        var fakeChecker = new FakeVersionChecker(new ReleaseInfo("v2.0.0", "v2.0.0", "https://example.com", "2026-10-01", false));
        var vm = new AboutViewModel(fakeChecker);

        Assert.Equal("AIMonitor 2.0", vm.AppName);
        Assert.Equal("Apichart Chantanis", vm.DeveloperName);
        Assert.Equal("apichart@apichart.net", vm.DeveloperEmail);
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", vm.LicenseText);
        Assert.Contains("AI Quota & Usage Monitor", vm.ReadmeText);
        Assert.NotNull(vm.OpenProjectUrlCommand);
        Assert.NotNull(vm.OpenReleaseUrlCommand);
    }

    [Fact]
    public async Task CheckUpdatesAsync_WhenNewer_SetsUpdateAvailableTrue()
    {
        var release = new ReleaseInfo("v2.5.0", "v2.5.0", "https://example.com/release/2.5.0", "2026-10-15", true);
        var fakeChecker = new FakeVersionChecker(release);

        var vm = new AboutViewModel(fakeChecker);
        await vm.CheckUpdatesAsync();

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal("Get the update", vm.ReleaseButtonText);
        Assert.Contains("v2.5.0", vm.UpdateStatus);
        Assert.Same(release, vm.LatestRelease);
    }

    [Fact]
    public async Task GetTheUpdate_HandsTheReleaseToTheInstaller_InsteadOfOpeningTheWebPage()
    {
        var release = new ReleaseInfo("v2.5.0", "v2.5.0", "https://example.com/release/2.5.0", "", true);
        var vm = new AboutViewModel(new FakeVersionChecker(release));
        ReleaseInfo? requested = null;
        vm.RequestInstall = r => requested = r;
        await vm.CheckUpdatesAsync();

        vm.OpenReleaseUrlCommand.Execute(null);

        Assert.Same(release, requested);
    }

    [Fact]
    public async Task CheckUpdatesAsync_WhenNotNewer_SetsUpdateAvailableFalse()
    {
        var release = new ReleaseInfo("v2.0.0", "v2.0.0", "https://example.com", "2026-10-01", false);
        var fakeChecker = new FakeVersionChecker(release);

        var vm = new AboutViewModel(fakeChecker);
        await vm.CheckUpdatesAsync();

        Assert.False(vm.IsUpdateAvailable);
        Assert.Contains("up to date", vm.UpdateStatus);
        Assert.Equal("Open releases page", vm.ReleaseButtonText);
    }

    [Fact]
    public void Opening_DoesNotCallGitHub_UntilTheUserAsks()
    {
        var checker = new CountingChecker();
        var vm = new AboutViewModel(checker);

        Assert.Equal(0, checker.Calls);
        Assert.Contains("read-only request to GitHub", vm.UpdateStatus);
        Assert.Equal("Check for updates", vm.CheckButtonText);
    }

    [Fact]
    public async Task CheckFailure_IsReportedInTheStatusLine()
    {
        var vm = new AboutViewModel(new ThrowingChecker());
        await vm.CheckUpdatesAsync();

        Assert.False(vm.IsUpdateAvailable);
        Assert.Equal("GitHub is rate-limiting this check. Try again later.", vm.UpdateStatus);
        Assert.False(vm.IsChecking);
    }

    private sealed class CountingChecker : IVersionChecker
    {
        public int Calls { get; private set; }

        public Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReleaseInfo("v2.0.0", "v2.0.0", "https://example.com", "", false));
        }
    }

    private sealed class ThrowingChecker : IVersionChecker
    {
        public Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default) =>
            throw new UpdateCheckException("GitHub is rate-limiting this check. Try again later.");
    }

    private sealed class FakeVersionChecker(ReleaseInfo releaseToReturn) : IVersionChecker
    {
        public Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(releaseToReturn);
    }
}
