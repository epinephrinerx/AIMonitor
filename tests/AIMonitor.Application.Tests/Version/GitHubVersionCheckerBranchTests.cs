using System.Net;
using AIMonitor.Application.Version;

namespace AIMonitor.Application.Tests.Version;

/// <summary>
/// The macOS edition only takes releases from its own branch. The fixtures follow the real
/// repository: v2.0.3 is a Windows release from main, while v1.3.1 and v1.1.0 are tags on Mac.
/// </summary>
public sealed class GitHubVersionCheckerBranchTests
{
    private const string Releases = """
    [
        { "tag_name": "v2.0.3", "name": "AIMonitor 2.0.3", "target_commitish": "main",
          "html_url": "https://github.com/epinephrinerx/AIMonitor/releases/tag/v2.0.3",
          "published_at": "2026-11-10T14:45:18Z",
          "assets": [ { "name": "AIUsageMonitor.Setup.2.0.3.exe", "browser_download_url": "https://github.com/x/Setup.exe", "size": 1 } ] },
        { "tag_name": "v1.3.1", "name": "AIMonitor 1.3.1", "target_commitish": "main",
          "html_url": "https://github.com/epinephrinerx/AIMonitor/releases/tag/v1.3.1",
          "published_at": "2026-09-18T05:10:17Z",
          "assets": [ { "name": "AIUsageMonitor-Setup-1.3.1.exe", "browser_download_url": "https://github.com/x/Setup-1.3.1.exe", "size": 1 } ] },
        { "tag_name": "v1.1.0", "name": "AIMonitor 1.1.0", "target_commitish": "Mac",
          "html_url": "https://github.com/epinephrinerx/AIMonitor/releases/tag/v1.1.0",
          "published_at": "2026-09-12T09:54:53Z" }
    ]
    """;

    private static readonly string[] TagsOnMac = ["v1.3.1", "v1.1.0"];

    [Fact]
    public async Task TheNewestReleaseOnTheBranchWins_EvenWhenMainHasANewerOne()
    {
        var github = new FakeGitHub(Releases, TagsOnMac);

        var release = await Checker(github).CheckLatestAsync("1.2.0");

        Assert.Equal("v1.3.1", release.Tag);
        Assert.Equal("AIMonitor 1.3.1", release.Name);
        Assert.Equal("https://github.com/epinephrinerx/AIMonitor/releases/tag/v1.3.1", release.Url);
        Assert.Equal("2026-09-18", release.Published);
        Assert.True(release.IsNewer);
    }

    [Fact]
    public async Task AWindowsReleaseFromMainIsNeverOffered()
    {
        var github = new FakeGitHub(Releases, TagsOnMac);

        var release = await Checker(github).CheckLatestAsync("2.0.0");

        Assert.Equal("v1.3.1", release.Tag);
        Assert.False(release.IsNewer);
    }

    [Fact]
    public async Task TheBranchIsDecidedByHistory_NotByTargetCommitish()
    {
        // v1.3.1 names main as its target but its tag is on Mac; v2.0.3 names main and is not.
        var github = new FakeGitHub(Releases, TagsOnMac);

        await Checker(github).CheckLatestAsync("2.0.0");

        Assert.Contains(github.Requests, path => path.EndsWith("/compare/Mac...v2.0.3", StringComparison.Ordinal));
        Assert.Contains(github.Requests, path => path.EndsWith("/compare/Mac...v1.3.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLatestReleaseEndpointIsNotUsed()
    {
        var github = new FakeGitHub(Releases, TagsOnMac);

        await Checker(github).CheckLatestAsync("2.0.0");

        Assert.DoesNotContain(github.Requests, path => path.EndsWith("/releases/latest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoInstallerIsOffered_BecauseTheDownloaderOnlyFetchesWindowsSetupPrograms()
    {
        var github = new FakeGitHub(Releases, TagsOnMac);

        var release = await Checker(github).CheckLatestAsync("1.0.0");

        Assert.False(release.HasInstaller);
    }

    [Fact]
    public async Task DraftsAndPrereleasesOnTheBranchAreSkipped()
    {
        const string releases = """
        [
            { "tag_name": "v2.2.0", "draft": true },
            { "tag_name": "v2.1.0", "prerelease": true },
            { "tag_name": "v2.0.0" }
        ]
        """;
        var github = new FakeGitHub(releases, ["v2.2.0", "v2.1.0", "v2.0.0"]);

        var release = await Checker(github).CheckLatestAsync("1.0.0");

        Assert.Equal("v2.0.0", release.Tag);
    }

    [Fact]
    public async Task ATagGitHubCannotCompareIsTreatedAsNotOnTheBranch()
    {
        var github = new FakeGitHub(Releases, TagsOnMac) { MissingTags = { "v2.0.3" } };

        var release = await Checker(github).CheckLatestAsync("1.0.0");

        Assert.Equal("v1.3.1", release.Tag);
    }

    [Fact]
    public async Task NoReleaseOnTheBranchIsReportedRatherThanFallingBackToMain()
    {
        var github = new FakeGitHub(Releases, tagsOnBranch: []);

        var exception = await Assert.ThrowsAsync<UpdateCheckException>(() => Checker(github).CheckLatestAsync("2.0.0"));

        Assert.Equal("No release has been published from the Mac branch yet.", exception.Message);
    }

    [Fact]
    public async Task TheCheckGivesUpAfterABoundedNumberOfComparisons()
    {
        var releases = "[" + string.Join(",", Enumerable.Range(1, GitHubVersionChecker.MaxBranchComparisons + 5)
            .Select(minor => $$"""{ "tag_name": "v3.{{minor}}.0" }""")) + "]";
        var github = new FakeGitHub(releases, tagsOnBranch: []);

        var exception = await Assert.ThrowsAsync<UpdateCheckException>(() => Checker(github).CheckLatestAsync("2.0.0"));

        Assert.Contains("among the newest", exception.Message);
        Assert.Equal(GitHubVersionChecker.MaxBranchComparisons, github.Requests.Count(path => path.Contains("/compare/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARateLimitedComparisonSaysSo()
    {
        var github = new FakeGitHub(Releases, TagsOnMac) { CompareStatus = HttpStatusCode.Forbidden };

        var exception = await Assert.ThrowsAsync<UpdateCheckException>(() => Checker(github).CheckLatestAsync("2.0.0"));

        Assert.Contains("rate-limiting", exception.Message);
    }

    [Fact]
    public async Task AHiddenRepositorySaysSo()
    {
        var github = new FakeGitHub(Releases, TagsOnMac) { ReleasesStatus = HttpStatusCode.NotFound };

        var exception = await Assert.ThrowsAsync<UpdateCheckException>(() => Checker(github).CheckLatestAsync("2.0.0"));

        Assert.Contains("anonymous request", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WithoutABranchTheLatestReleaseIsUsed_AsTheWindowsEditionDoes(string? branch)
    {
        var github = new FakeGitHub(Releases, TagsOnMac)
        {
            Latest = """{ "tag_name": "v2.0.3", "assets": [ { "name": "AIUsageMonitor.Setup.2.0.3.exe", "browser_download_url": "https://github.com/x/Setup.exe", "size": 1 } ] }""",
        };
        using var client = new HttpClient(github);

        var release = await new GitHubVersionChecker(client, branch: branch).CheckLatestAsync("2.0.0");

        Assert.Equal("v2.0.3", release.Tag);
        Assert.True(release.HasInstaller);
        Assert.DoesNotContain(github.Requests, path => path.Contains("/compare/", StringComparison.Ordinal));
    }

    private static GitHubVersionChecker Checker(FakeGitHub github) =>
        new(new HttpClient(github), branch: "Mac");

    /// <summary>Answers the three GitHub endpoints the checker uses, and records what it asked.</summary>
    private sealed class FakeGitHub(string releases, string[] tagsOnBranch) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public HashSet<string> MissingTags { get; } = [];

        public HttpStatusCode ReleasesStatus { get; init; } = HttpStatusCode.OK;

        public HttpStatusCode CompareStatus { get; init; } = HttpStatusCode.OK;

        public string Latest { get; init; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Requests.Add(path);

            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return Answer(HttpStatusCode.OK, Latest);
            }

            if (path.EndsWith("/releases", StringComparison.Ordinal))
            {
                return Answer(ReleasesStatus, releases);
            }

            var compare = path.IndexOf("/compare/Mac...", StringComparison.Ordinal);
            if (compare >= 0)
            {
                var tag = path[(compare + "/compare/Mac...".Length)..];
                if (MissingTags.Contains(tag)) return Answer(HttpStatusCode.NotFound, "{}");

                var status = tagsOnBranch.Contains(tag) ? "behind" : "diverged";
                return Answer(CompareStatus, $$"""{ "status": "{{status}}" }""");
            }

            return Answer(HttpStatusCode.NotFound, "{}");
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string content) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
    }
}
