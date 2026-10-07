namespace AIMonitor.Application.Version;

public interface IUpdateDownloader
{
    /// <summary>Fetches the release's installer into a temp folder and returns its path.</summary>
    Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken = default);
}

/// <summary>Downloads the installer straight from the GitHub release, so nobody has to visit the web page.</summary>
public sealed class UpdateDownloader : IUpdateDownloader
{
    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    private readonly HttpClient _httpClient;
    private readonly string _folder;

    public UpdateDownloader(HttpClient? httpClient = null, string? folder = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _folder = folder ?? Path.Combine(Path.GetTempPath(), "AIUsageMonitor-update");
    }

    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (!release.HasInstaller)
        {
            throw new UpdateCheckException("This release has no installer attached.");
        }

        if (!Uri.TryCreate(release.AssetUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new UpdateCheckException("The installer is not hosted on GitHub, so it will not be downloaded.");
        }

        // Path.GetFileName drops any directory part a hostile asset name might carry.
        var fileName = Path.GetFileName(release.AssetName);
        if (fileName.Length == 0 || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateCheckException("The release's installer has an unexpected name.");
        }

        Directory.CreateDirectory(_folder);
        var target = Path.Combine(_folder, fileName);
        var partial = target + ".part";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("User-Agent", "AIUsageMonitor");
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException($"GitHub returned HTTP {(int)response.StatusCode} for the installer.");
            }

            var total = response.Content.Headers.ContentLength ?? release.AssetSize;
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(1.0, (double)received / total));
                    }
                }
            }

            if (release.AssetSize > 0 && received != release.AssetSize)
            {
                throw new UpdateCheckException(
                    $"The download is incomplete ({received:N0} of {release.AssetSize:N0} bytes). Nothing was installed.");
            }

            File.Move(partial, target, overwrite: true);
            progress?.Report(1.0);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException($"Could not download the installer: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
            catch (IOException)
            {
                // A leftover .part file is overwritten by the next attempt.
            }
        }
    }
}
