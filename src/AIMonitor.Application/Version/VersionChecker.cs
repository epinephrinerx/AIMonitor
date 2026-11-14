using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIMonitor.Application.Version;

public sealed record ReleaseInfo(
    string Tag,
    string Name,
    string Url,
    string Published,
    bool IsNewer,
    string AssetName = "",
    string AssetUrl = "",
    long AssetSize = 0)
{
    /// <summary>True when the release carries an installer the app can fetch itself.</summary>
    public bool HasInstaller => AssetUrl.Length > 0;
}

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

    /// <summary>
    /// How many releases are tested against the branch before the check gives up. Each test is one
    /// anonymous request, and GitHub allows sixty of those an hour.
    /// </summary>
    public const int MaxBranchComparisons = 10;

    private readonly HttpClient _httpClient;
    private readonly string _repo;
    private readonly string? _branch;

    /// <param name="branch">
    /// When set, only releases whose tag is part of this branch's history count. The macOS edition
    /// passes its own branch, because every release from main is a Windows build it cannot install.
    /// When null, the repository's latest release is used, as the Windows edition always has.
    /// </param>
    public GitHubVersionChecker(HttpClient? httpClient = null, string repo = DefaultRepo, string? branch = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
        _branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
    }

    public async Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default)
    {
        if (_branch is not null)
        {
            return await CheckLatestOnBranchAsync(_branch, installedVersion, cancellationToken).ConfigureAwait(false);
        }

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
            var (assetName, assetUrl, assetSize) = PickInstaller(root);
            return new ReleaseInfo(tag, name, htmlUrl, published, newer, assetName, assetUrl, assetSize);
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException("Could not read GitHub's answer.", ex);
        }
    }

    /// <summary>The installer asset of a release: the *Setup*.exe, else any .exe.</summary>
    private static (string Name, string Url, long Size) PickInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return ("", "", 0);
        }

        (string Name, string Url, long Size)? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            var size = asset.TryGetProperty("size", out var z) && z.TryGetInt64(out var parsed) ? parsed : 0;
            if (url.Length == 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                return (name, url, size);
            }

            fallback ??= (name, url, size);
        }

        return fallback ?? ("", "", 0);
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

    /// <summary>
    /// The newest published release whose tag lies on <paramref name="branch"/>. A release's
    /// <c>target_commitish</c> cannot decide this: GitHub ignores it whenever the tag already
    /// existed, so it often names main or a bare commit. Asking GitHub to compare the branch with
    /// the tag can: "behind" or "identical" means the tag is in the branch's history.
    /// </summary>
    private async Task<ReleaseInfo> CheckLatestOnBranchAsync(string branch, string installedVersion, CancellationToken cancellationToken)
    {
        var releasesUrl = $"https://api.github.com/repos/{_repo}/releases?per_page=100";
        var candidates = new List<(string Tag, string Name, string Url, string Published)>();

        using (var response = await SendAsync(releasesUrl, installedVersion, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateCheckException("GitHub does not show this repository to an anonymous request, so the update check cannot see its releases.");
            }

            ThrowIfFailed(response);
            try
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new UpdateCheckException("Could not read GitHub's answer.");
                }

                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (IsTrue(release, "draft") || IsTrue(release, "prerelease")) continue;

                    var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                    if (ParseVersion(tag).Length == 0) continue;

                    var name = release.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()! : tag;
                    var url = release.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
                    if (url.Length == 0) url = $"https://github.com/{_repo}/releases/tag/{tag}";
                    var published = release.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "";
                    if (published.Length >= 10) published = published[..10];

                    candidates.Add((tag, name, url, published));
                }
            }
            catch (JsonException ex)
            {
                throw new UpdateCheckException("Could not read GitHub's answer.", ex);
            }
        }

        var comparisons = 0;
        foreach (var candidate in candidates.OrderByDescending(c => ParseVersion(c.Tag), new VersionTupleComparer()))
        {
            if (comparisons++ == MaxBranchComparisons)
            {
                throw new UpdateCheckException($"Could not find a release from the {branch} branch among the newest {MaxBranchComparisons} releases.");
            }

            if (await IsOnBranchAsync(branch, candidate.Tag, installedVersion, cancellationToken).ConfigureAwait(false))
            {
                // No installer is offered: the downloader only fetches Windows setup programs, and
                // the macOS edition sends the user to the release page instead.
                return new ReleaseInfo(
                    candidate.Tag,
                    candidate.Name,
                    candidate.Url,
                    candidate.Published,
                    IsNewer(candidate.Tag, installedVersion));
            }
        }

        throw new UpdateCheckException($"No release has been published from the {branch} branch yet.");
    }

    private async Task<bool> IsOnBranchAsync(string branch, string tag, string installedVersion, CancellationToken cancellationToken)
    {
        var compareUrl = $"https://api.github.com/repos/{_repo}/compare/{Uri.EscapeDataString(branch)}...{Uri.EscapeDataString(tag)}";
        using var response = await SendAsync(compareUrl, installedVersion, cancellationToken).ConfigureAwait(false);

        // A tag GitHub cannot resolve is simply not on the branch; a missing branch is not either.
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        ThrowIfFailed(response);

        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            return status is "behind" or "identical";
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException("Could not read GitHub's answer.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string installedVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", $"AIUsageMonitor/{installedVersion}");
        request.Headers.Add("Accept", "application/vnd.github+json");

        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException($"Could not reach GitHub: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException("GitHub request timed out.", ex);
        }
    }

    private static void ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            throw new UpdateCheckException("GitHub is rate-limiting this check. Try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateCheckException($"GitHub returned HTTP {(int)response.StatusCode}.");
        }
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

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
