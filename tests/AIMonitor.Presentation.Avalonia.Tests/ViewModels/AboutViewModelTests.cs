using AIMonitor.Application.Version;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

public sealed class AboutViewModelTests
{
    private sealed class StubChecker(Func<string, ReleaseInfo> answer) : IVersionChecker
    {
        public List<string> AskedAbout { get; } = [];

        public Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default)
        {
            AskedAbout.Add(installedVersion);
            return Task.FromResult(answer(installedVersion));
        }
    }

    private static ReleaseInfo Release(bool isNewer, string name = "AI Usage Monitor 2.1.0") =>
        new("v2.1.0", name, "https://github.com/x/releases/tag/v2.1.0", "2026-10-31", isNewer);

    [Fact]
    public void NamesTheAppItsEditionAndWhoWroteIt()
    {
        var about = new AboutViewModel();

        Assert.Equal("AI Usage Monitor", about.AppName);
        Assert.Equal("macOS Edition", about.Edition);
        Assert.Equal("Apichart Chantanis", about.DeveloperName);
    }

    [Fact]
    public void ReportsAVersion_NotAnEmptyField()
    {
        Assert.NotEmpty(new AboutViewModel().AppVersion);
    }

    [Fact]
    public void TheVersionCarriesNoBuildMetadata()
    {
        // "2.0.2+0a1b2c3" is for a bug report, not for the About box.
        Assert.DoesNotContain("+", new AboutViewModel().AppVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysWhichLicenceTheAppIsUnder()
    {
        Assert.Contains("GNU General Public License v3.0", new AboutViewModel().LicenseSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckingForUpdates_AsksAboutTheVersionThatIsRunning()
    {
        var checker = new StubChecker(_ => Release(isNewer: false));
        var about = new AboutViewModel(checker);

        await about.CheckUpdatesAsync();

        Assert.Equal(about.AppVersion, Assert.Single(checker.AskedAbout));
    }

    [Fact]
    public async Task ANewerReleaseIsNamed()
    {
        var about = new AboutViewModel(new StubChecker(_ => Release(isNewer: true)));

        await about.CheckUpdatesAsync();

        Assert.Equal("AI Usage Monitor 2.1.0 is available.", about.UpdateStatus);
        Assert.Equal("Open the new release", about.ReleaseButtonText);
    }

    [Fact]
    public async Task BeingUpToDateIsSaidPlainly()
    {
        var about = new AboutViewModel(new StubChecker(_ => Release(isNewer: false)));

        await about.CheckUpdatesAsync();

        Assert.Equal("This is the newest release.", about.UpdateStatus);
        Assert.Equal("Open the releases page", about.ReleaseButtonText);
    }

    [Fact]
    public async Task AFailedCheckShowsTheReasonInsteadOfThrowing()
    {
        var about = new AboutViewModel(new StubChecker(_ => throw new UpdateCheckException("GitHub did not answer.")));

        await about.CheckUpdatesAsync();

        Assert.Equal("GitHub did not answer.", about.UpdateStatus);
        Assert.False(about.IsChecking);
    }

    [Fact]
    public void WithNoWayToCheck_TheButtonIsNotOffered()
    {
        Assert.False(new AboutViewModel().CheckUpdatesCommand.CanExecute(null));
    }

    [Fact]
    public void OpeningTheProjectPage_HandsTheUrlToTheWindow()
    {
        var about = new AboutViewModel();
        string? asked = null;
        about.OpenUrlRequested += url => asked = url;

        about.OpenProjectCommand.Execute(null);

        Assert.Equal("https://github.com/epinephrinerx/AIMonitor", asked);
    }

    [Fact]
    public async Task OpeningReleases_PrefersTheReleaseThatWasFound()
    {
        var about = new AboutViewModel(new StubChecker(_ => Release(isNewer: true)));
        await about.CheckUpdatesAsync();
        string? asked = null;
        about.OpenUrlRequested += url => asked = url;

        about.OpenReleasesCommand.Execute(null);

        Assert.Equal("https://github.com/x/releases/tag/v2.1.0", asked);
    }

    [Fact]
    public void WithNothingFoundYet_OpeningReleasesGoesToTheReleasesPage()
    {
        var about = new AboutViewModel();
        string? asked = null;
        about.OpenUrlRequested += url => asked = url;

        about.OpenReleasesCommand.Execute(null);

        Assert.Equal("https://github.com/epinephrinerx/AIMonitor/releases", asked);
    }
}
