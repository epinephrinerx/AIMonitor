using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIMonitor.Application.Version;

public sealed record ReleaseInfo(
    string Tag,
    string Name,
    string Url,
    string Published,
    bool IsNewer);

public class UpdateCheckException : Exception
{
    public UpdateCheckException(string message) : base(message) { }
    public UpdateCheckException(string message, Exception inner) : base(message, inner) { }
}

public interface IVersionChecker
{
    Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default);
}

public sealed partial class GitHubVersionChecker : IVersionChecker
{
    public const string DefaultRepo = "epinephrinerx/AIMonitor";
    private readonly HttpClient _httpClient;
    private readonly string _repo;

    public GitHubVersionChecker(HttpClient? httpClient = null, string repo = DefaultRepo)
    {
        _httpClient = httpClient ?? new HttpClient();
        _repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
    }

    public async Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default)
    {
        var latestUrl = $"https://api.github.com/repos/{_repo}/releases/latest";
        var tagsUrl = $"https://api.github.com/repos/{_repo}/tags?per_page=100";

        using var request = new HttpRequestMessage(HttpMethod.Get, latestUrl);
        request.Headers.Add("User-Agent", $"AIUsageMonitor/{installedVersion}");
        request.Headers.Add("Accept", "application/vnd.github+json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException($"Could not reach GitHub: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException("GitHub request timed out.", ex);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Fall back to tags API
            return await CheckLatestTagAsync(tagsUrl, installedVersion, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            throw new UpdateCheckException("GitHub is rate-limiting this check. Try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateCheckException($"GitHub returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new UpdateCheckException("The newest release carries no version tag.");
            }

            var name = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? tag : tag;
            var htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? $"https://github.com/{_repo}/releases" : $"https://github.com/{_repo}/releases";
            var published = root.TryGetProperty("published_at", out var pubElem) ? (pubElem.GetString() ?? "") : "";
            if (published.Length >= 10) published = published[..10];

            var newer = IsNewer(tag, installedVersion);
            return new ReleaseInfo(tag, name, htmlUrl, published, newer);
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException("Could not read GitHub's answer.", ex);
        }
    }

    private async Task<ReleaseInfo> CheckLatestTagAsync(string tagsUrl, string installedVersion, CancellationToken cancellationToken)
    {
        using var tagReq = new HttpRequestMessage(HttpMethod.Get, tagsUrl);
        tagReq.Headers.Add("User-Agent", $"AIUsageMonitor/{installedVersion}");
        tagReq.Headers.Add("Accept", "application/vnd.github+json");

        HttpResponseMessage tagResp;
        try
        {
            tagResp = await _httpClient.SendAsync(tagReq, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateCheckException($"Could not reach GitHub: {ex.Message}", ex);
        }

        if (tagResp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateCheckException("GitHub does not show this repository to an anonymous request, so the update check cannot see its releases.");
        }

        if (tagResp.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            throw new UpdateCheckException("GitHub is rate-limiting this check. Try again later.");
        }

        if (!tagResp.IsSuccessStatusCode)
        {
            throw new UpdateCheckException($"GitHub returned HTTP {(int)tagResp.StatusCode}.");
        }

        try
        {
            var content = await tagResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new UpdateCheckException("Could not read GitHub's answer.");
            }

            var tags = new List<string>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (elem.TryGetProperty("name", out var n) && n.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                {
                    tags.Add(s);
                }
            }

            if (tags.Count == 0)
            {
                throw new UpdateCheckException("This project has no published releases yet.");
            }

            var highestTag = tags.MaxBy(t => ParseVersion(t), new VersionTupleComparer());
            if (highestTag is null)
            {
                throw new UpdateCheckException("This project has no valid version tags.");
            }

            var newer = IsNewer(highestTag, installedVersion);
            return new ReleaseInfo(
                Tag: highestTag,
                Name: highestTag,
                Url: $"https://github.com/{_repo}/releases/tag/{highestTag}",
                Published: "",
                IsNewer: newer);
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException("Could not read GitHub's answer.", ex);
        }
    }

    public static int[] ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var match = VersionRegex().Match(text);
        if (!match.Success) return [];

        var parts = match.Groups[1].Value.Split('.');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out result[i])) return [];
        }
        return result;
    }

    public static bool IsNewer(string candidate, string installed)
    {
        var cand = ParseVersion(candidate);
        var inst = ParseVersion(installed);

        if (cand.Length == 0 || inst.Length == 0) return false;

        var width = Math.Max(cand.Length, inst.Length);
        for (var i = 0; i < width; i++)
        {
            var c = i < cand.Length ? cand[i] : 0;
            var ins = i < inst.Length ? inst[i] : 0;
            if (c > ins) return true;
            if (c < ins) return false;
        }

        return false;
    }

    private sealed class VersionTupleComparer : IComparer<int[]>
    {
        public int Compare(int[]? x, int[]? y)
        {
            x ??= [];
            y ??= [];
            var width = Math.Max(x.Length, y.Length);
            for (var i = 0; i < width; i++)
            {
                var xv = i < x.Length ? x[i] : 0;
                var yv = i < y.Length ? y[i] : 0;
                if (xv != yv) return xv.CompareTo(yv);
            }
            return 0;
        }
    }

    [GeneratedRegex(@"\s*v?(\d+(?:\.\d+)*)")]
    private static partial Regex VersionRegex();
}
