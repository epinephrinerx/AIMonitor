using System.Net;
using AIMonitor.Application.Version;

namespace AIMonitor.Application.Tests.Version;

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AIMonitorUpdateTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static ReleaseInfo Release(string url, string name, long size) =>
        new("v2.1.0", "v2.1.0", "https://github.com/x", "", true, name, url, size);

    private UpdateDownloader Downloader(byte[] payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new FakeHandler(payload, status)), _folder);

    [Fact]
    public async Task Download_WritesTheInstallerAndReportsProgress()
    {
        var payload = new byte[200_000];
        new Random(1).NextBytes(payload);
        var reports = new List<double>();

        var path = await Downloader(payload).DownloadAsync(
            Release("https://github.com/x/Setup.exe", "Setup-2.1.0.exe", payload.Length),
            new Progress<double>(reports.Add));

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(_folder, Path.GetDirectoryName(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task Download_ShortFile_IsRejectedAndLeavesNothing()
    {
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => Downloader(new byte[100]).DownloadAsync(
            Release("https://github.com/x/Setup.exe", "Setup-2.1.0.exe", 5000), null));

        Assert.Contains("incomplete", ex.Message);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("http://github.com/x/Setup.exe")]
    [InlineData("https://evil.example.com/Setup.exe")]
    [InlineData("not a url")]
    public async Task Download_RefusesNonGitHubOrNonHttpsUrls(string url)
    {
        await Assert.ThrowsAsync<UpdateCheckException>(() => Downloader(new byte[10]).DownloadAsync(
            Release(url, "Setup-2.1.0.exe", 10), null));
    }

    [Fact]
    public async Task Download_StripsDirectoriesFromTheAssetName()
    {
        var path = await Downloader(new byte[10]).DownloadAsync(
            Release("https://github.com/x/Setup.exe", @"..\..\evil\Setup-2.1.0.exe", 10), null);

        Assert.Equal(_folder, Path.GetDirectoryName(path));
        Assert.Equal("Setup-2.1.0.exe", Path.GetFileName(path));
    }

    [Fact]
    public async Task Download_NonExeName_IsRefused()
    {
        await Assert.ThrowsAsync<UpdateCheckException>(() => Downloader(new byte[10]).DownloadAsync(
            Release("https://github.com/x/a.zip", "a.zip", 10), null));
    }

    [Fact]
    public async Task Download_HttpError_IsReported()
    {
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => Downloader([], HttpStatusCode.NotFound).DownloadAsync(
            Release("https://github.com/x/Setup.exe", "Setup-2.1.0.exe", 10), null));

        Assert.Contains("404", ex.Message);
    }

    private sealed class FakeHandler(byte[] payload, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(payload) });
    }
}
