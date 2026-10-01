using System.Net;
using AIMonitor.Application.Version;

namespace AIMonitor.Application.Tests.Version;

public class GitHubVersionCheckerTests
{
    [Theory]
    [InlineData("1.0.0", new[] { 1, 0, 0 })]
    [InlineData("v2.1.3", new[] { 2, 1, 3 })]
    [InlineData("v0.9", new[] { 0, 9 })]
    [InlineData("invalid", new int[0])]
    [InlineData("", new int[0])]
    public void ParseVersion_ParsesValidStrings(string input, int[] expected)
    {
        var result = GitHubVersionChecker.ParseVersion(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("v2.0.1", "2.0.0", true)]
    [InlineData("v2.0.0", "2.0.0", false)]
    [InlineData("1.3", "1.3.0", false)]
    [InlineData("1.4.0", "1.3.9", true)]
    [InlineData("v1.0", "2.0", false)]
    [InlineData("bad-tag", "1.0", false)]
    public void IsNewer_ComparesSemanticVersions(string candidate, string installed, bool expected)
    {
        var result = GitHubVersionChecker.IsNewer(candidate, installed);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task CheckLatestAsync_ReleaseEndpoint200_ReturnsParsedRelease()
    {
        var jsonResponse = """
        {
            "tag_name": "v2.1.0",
            "name": "AIMonitor 2.1.0",
            "html_url": "https://github.com/epinephrinerx/AIMonitor/releases/tag/v2.1.0",
            "published_at": "2026-10-15T08:00:00Z"
        }
        """;

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var client = new HttpClient(handler);
        var checker = new GitHubVersionChecker(client);

        var release = await checker.CheckLatestAsync("2.0.0");

        Assert.Equal("v2.1.0", release.Tag);
        Assert.Equal("AIMonitor 2.1.0", release.Name);
        Assert.Equal("https://github.com/epinephrinerx/AIMonitor/releases/tag/v2.1.0", release.Url);
        Assert.Equal("2026-10-15", release.Published);
        Assert.True(release.IsNewer);
    }

    [Fact]
    public async Task CheckLatestAsync_ReleaseEndpoint404_FallsBackToTags()
    {
        var tagsJson = """
        [
            { "name": "v1.9.0" },
            { "name": "v2.0.5" },
            { "name": "v2.0.1" }
        ]
        """;

        var handler = new DynamicMockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tagsJson) };
        });

        using var client = new HttpClient(handler);
        var checker = new GitHubVersionChecker(client);

        var release = await checker.CheckLatestAsync("2.0.0");

        Assert.Equal("v2.0.5", release.Tag);
        Assert.True(release.IsNewer);
    }

    [Fact]
    public async Task CheckLatestAsync_RateLimited_ThrowsUpdateCheckException()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.Forbidden, "rate limit exceeded");
        using var client = new HttpClient(handler);
        var checker = new GitHubVersionChecker(client);

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => checker.CheckLatestAsync("2.0.0"));
        Assert.Contains("rate-limiting", ex.Message);
    }

    private sealed class MockHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode) { Content = new StringContent(content) };
            return Task.FromResult(response);
        }
    }

    private sealed class DynamicMockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
