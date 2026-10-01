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
    public void CheckUpdatesAsync_WhenNewer_SetsUpdateAvailableTrue()
    {
        var release = new ReleaseInfo("v2.5.0", "v2.5.0", "https://example.com/release/2.5.0", "2026-10-15", true);
        var fakeChecker = new FakeVersionChecker(release);

        var vm = new AboutViewModel(fakeChecker);

        Assert.True(vm.IsUpdateAvailable);
        Assert.Contains("v2.5.0", vm.UpdateStatus);
        Assert.Same(release, vm.LatestRelease);
    }

    [Fact]
    public void CheckUpdatesAsync_WhenNotNewer_SetsUpdateAvailableFalse()
    {
        var release = new ReleaseInfo("v2.0.0", "v2.0.0", "https://example.com", "2026-10-01", false);
        var fakeChecker = new FakeVersionChecker(release);

        var vm = new AboutViewModel(fakeChecker);

        Assert.False(vm.IsUpdateAvailable);
        Assert.Contains("latest version", vm.UpdateStatus);
    }

    private sealed class FakeVersionChecker(ReleaseInfo releaseToReturn) : IVersionChecker
    {
        public Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(releaseToReturn);
    }
}
