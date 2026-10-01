using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// Application's <see cref="IProviderQuotaClient"/> port for Gemini. Resolves the credential via
/// <see cref="GeminiCredentialResolver"/> (PAR-011/PAR-012), mints a bearer token appropriate to
/// whichever credential kind won (a self-signed JWT exchanged for a service-account access token, or a
/// Gemini CLI OAuth token used as-is - never refreshed), resolves a Google Cloud project, and reads
/// request-count usage from Cloud Monitoring. Only <see cref="System.Text.Json"/>,
/// <see cref="System.Net.Http"/>, and <see cref="System.Security.Cryptography"/> are used - no new
/// NuGet dependency, matching the Claude/OpenAI slices.
/// </summary>
public sealed class GeminiLiveQuotaClient : IProviderQuotaClient
{
    private const string ProviderId = GeminiCredentialResolver.ProviderId;

    private const string SetupHint =
        "Detected automatically from your Gemini CLI login if you have signed in with `gemini` - that " +
        "token already carries the cloud-platform scope this needs. Otherwise point this at a Google " +
        "Cloud service account JSON with the Monitoring Viewer role. Either way, usage comes from " +
        "Cloud Monitoring, because Google publishes no usage endpoint for Gemini. Gemini Advanced " +
        "subscription limits are not available from any public API.";

    private const string ValueNote = "Request counts from Cloud Monitoring; Google reports no token-level cost here.";

    private const string NoUsableCredentialMessage = "No usable Google credential was found.";

    private const string CouldNotReadServiceAccountFileMessage = "Could not read the service account file.";

    private const string NotAServiceAccountKeyMessage =
        "That JSON is not a service account key (expected \"type\": \"service_account\").";

    private const string CouldNotParsePrivateKeyMessage = "Could not parse the service account private key.";

    private const string NoAccessTokenMessage = "Google returned no access token.";

    private const string NoActiveProjectMessage =
        "This Google account has no active Cloud project. Create one and enable the Generative " +
        "Language API, then set the project id in Settings.";

    private const string RejectedMessage =
        "Google rejected the service account. Check it has the Monitoring Viewer role on the project.";

    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string TokenHost = "oauth2.googleapis.com";
    private const string MonitoringUrlTemplate = "https://monitoring.googleapis.com/v3/projects/{0}/timeSeries";
    private const string MonitoringHost = "monitoring.googleapis.com";

    private const string CloudResourceManagerUrl =
        "https://cloudresourcemanager.googleapis.com/v1/projects?filter=lifecycleState:ACTIVE&pageSize=50";

    private const string CloudResourceManagerHost = "cloudresourcemanager.googleapis.com";

    private const string MonitoringReadScope = "https://www.googleapis.com/auth/monitoring.read";
    private const string MonitoringServiceName = "generativelanguage.googleapis.com";
    private const string UserAgentValue = "AIUsageMonitor/2.0";

    private const string MonitoringFilter =
        "metric.type=\"serviceruntime.googleapis.com/api/request_count\" AND resource.labels.service=\"" +
        MonitoringServiceName + "\"";

    private const string RequestsSeriesLabel = "Requests";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxSupportedTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private const int DefaultMaxResponseBodyBytes = 1_000_000;
    private const int MaxHistoryDays = 400;
    private const int MaxMonitoringPages = 100;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly string _userProfileDirectory;
    private readonly IClock _clock;
    private readonly string? _savedServiceAccountPath;
    private readonly string? _projectOverride;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly TimeSpan _timeout;
    private readonly int _maxResponseBodyBytes;
    private readonly TimeZoneInfo _localTimeZone;

    /// <param name="httpClient">Caller-owned; this client sends requests through it but never disposes
    /// it.</param>
    /// <param name="userProfileDirectory">The current user's profile directory, used to resolve Gemini
    /// CLI's and gcloud's well-known credential subpaths.</param>
    /// <param name="savedServiceAccountPath">The service-account file path saved in this app's own
    /// settings, if any - supplied by the caller exactly like the Python baseline's <c>configure()</c>;
    /// this client does not read or know about a settings store.</param>
    /// <param name="projectOverride">An optional Google Cloud project id the user set explicitly
    /// (Settings' "extra" field); takes priority over a project id embedded in the credential, which in
    /// turn takes priority over auto-discovery.</param>
    /// <param name="environmentVariableReader">Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>;
    /// tests inject a fixed map so <c>GOOGLE_APPLICATION_CREDENTIALS</c> resolves deterministically.</param>
    /// <param name="timeout">Overrides the 20-second production default per HTTP request; must be
    /// positive, finite, and within what <see cref="CancellationTokenSource"/> supports.</param>
    /// <param name="maxResponseBodyBytes">Overrides the 1,000,000-byte production default cap on each
    /// response body; must be positive.</param>
    /// <param name="localTimeZone">The zone used for "today"/local-day boundaries. Defaults to
    /// <see cref="TimeZoneInfo.Local"/>; tests inject a fixed zone.</param>
    public GeminiLiveQuotaClient(
        HttpClient httpClient,
        string userProfileDirectory,
        IClock clock,
        string? savedServiceAccountPath = null,
        string? projectOverride = null,
        Func<string, string?>? environmentVariableReader = null,
        TimeSpan? timeout = null,
        int? maxResponseBodyBytes = null,
        TimeZoneInfo? localTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        if (maxResponseBodyBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResponseBodyBytes), maxResponseBodyBytes, "Response body cap must be positive.");
        }

        _httpClient = httpClient;
        _userProfileDirectory = userProfileDirectory;
        _clock = clock;
        _savedServiceAccountPath = savedServiceAccountPath;
        _projectOverride = projectOverride;
        _environmentVariableReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        _timeout = ValidateTimeout(timeout ?? DefaultTimeout);
        _maxResponseBodyBytes = maxResponseBodyBytes ?? DefaultMaxResponseBodyBytes;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be a positive duration.");
        }

        if (timeout > MaxSupportedTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "Timeout exceeds the maximum duration CancellationTokenSource supports.");
        }

        return timeout;
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked before any file or network access so an already-cancelled call touches neither.
        cancellationToken.ThrowIfCancellationRequested();

        var resolution = await GeminiCredentialResolver
            .ResolveAsync(_savedServiceAccountPath, _environmentVariableReader, _userProfileDirectory, _clock, cancellationToken)
            .ConfigureAwait(false);
        var detection = resolution.Detection;
        var credential = resolution.Credential;

        if (!detection.Usable || credential is null)
        {
            // Limited/NotConnected are communicated purely via Detection, matching the Python baseline:
            // only an Expired login is promoted to a hard `error` here.
            cancellationToken.ThrowIfCancellationRequested();
            var error = detection.State == DetectionState.Expired ? detection.Hint : null;
            return new ProviderSnapshot(
                ProviderId, configured: false, detection: detection, setupHint: SetupHint, valueNote: ValueNote, error: error);
        }

        string token;
        string project;
        try
        {
            token = await GetBearerTokenAsync(credential, cancellationToken).ConfigureAwait(false);
            project = await ResolveProjectAsync(credential, token, cancellationToken).ConfigureAwait(false);
        }
        catch (GeminiApiException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId, configured: true, detection: detection, setupHint: SetupHint, valueNote: ValueNote,
                error: ex.Message, unauthorized: ex.Unauthorized);
        }

        var fetchedAt = _clock.UtcNow;
        var who = credential.Account.Length > 0 ? credential.Account : "signed in";
        var account = $"{who} · project {project} · {detection.SourceLabel}";

        try
        {
            var localNow = TimeZoneInfo.ConvertTime(fetchedAt, _localTimeZone);
            var localToday = DateOnly.FromDateTime(localNow.DateTime);
            var dayStartUtc = LocalDayStartUtc(localToday, _localTimeZone);
            var todayTotal = await GetSeriesTotalAsync(token, project, dayStartUtc, fetchedAt, cancellationToken).ConfigureAwait(false);

            var meter = new Meter(
                kind: "requests_today",
                key: "requests_today",
                title: "Today",
                subtitle: "API requests",
                percent: null,
                detail: FormatCompactNumber(todayTotal),
                resetsAt: LocalDayStartUtc(localToday.AddDays(1), _localTimeZone));

            UsageHistory? history = null;
            IReadOnlyList<Stat> stats = [];
            string? historyError = null;

            if (request.IncludeHistory)
            {
                try
                {
                    var (builtHistory, rangeTotal) = await GetHistoryAsync(
                        token, project, request.HistoryDays, request.Metric, localToday, fetchedAt, cancellationToken)
                        .ConfigureAwait(false);
                    history = builtHistory;
                    stats =
                    [
                        new Stat("Requests in range", FormatCompactNumber(rangeTotal)),
                        new Stat("Requests today", FormatCompactNumber(todayTotal)),
                        new Stat("Project", project),
                    ];
                }
                catch (GeminiApiException ex)
                {
                    // Its own handler: the Today meter above is already read and good, and a failure
                    // fetching the range behind it must not be reported as the service having failed
                    // (PAR-004).
                    cancellationToken.ThrowIfCancellationRequested();
                    historyError = ex.Message;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId,
                configured: true,
                meters: [meter],
                stats: stats,
                history: history,
                account: account,
                historyError: historyError,
                fetchedAt: fetchedAt,
                detection: detection,
                setupHint: SetupHint,
                valueNote: ValueNote);
        }
        catch (GeminiApiException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId, configured: true, account: account, error: ex.Message, unauthorized: ex.Unauthorized,
                fetchedAt: fetchedAt, detection: detection, setupHint: SetupHint, valueNote: ValueNote);
        }
    }

    // -- bearer token / project resolution ---------------------------------

    private async Task<string> GetBearerTokenAsync(GeminiCredential credential, CancellationToken cancellationToken)
    {
        if (credential.Kind == GeminiCredentialKind.ServiceAccount)
        {
            return await MintServiceAccountTokenAsync(credential.Value, cancellationToken).ConfigureAwait(false);
        }

        if (credential.Kind == GeminiCredentialKind.OAuth && credential.Value.Length > 0)
        {
            // The Gemini CLI already holds a token with cloud-platform scope, so it is used directly.
            // This app never refreshes the CLI's token - that is its own business.
            return credential.Value;
        }

        throw new GeminiApiException(NoUsableCredentialMessage);
    }

    private async Task<string> MintServiceAccountTokenAsync(string path, CancellationToken cancellationToken)
    {
        var fileResult = await GeminiCredentialFile.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (fileResult.Status != GeminiCredentialFile.ReadStatus.Success)
        {
            throw new GeminiApiException(CouldNotReadServiceAccountFileMessage);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(fileResult.Text!);
        }
        catch (JsonException)
        {
            throw new GeminiApiException(CouldNotReadServiceAccountFileMessage);
        }

        using (document)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = GeminiServiceAccountKey.FromJsonObject(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object || key.Type != "service_account")
            {
                throw new GeminiApiException(NotAServiceAccountKeyMessage);
            }

            if (key.ClientEmail.Length == 0)
            {
                throw new GeminiApiException("Service account key is missing client_email.");
            }

            if (key.PrivateKey.Length == 0)
            {
                throw new GeminiApiException("Service account key is missing private_key.");
            }

            string assertion;
            try
            {
                assertion = SignServiceAccountAssertion(key.ClientEmail, key.PrivateKey);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
            {
                throw new GeminiApiException(CouldNotParsePrivateKeyMessage);
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            };

            var responseBody = await PostFormAsync(new Uri(TokenUrl), form, TokenHost, cancellationToken).ConfigureAwait(false);

            using var tokenDocument = ParseJsonOrThrow(responseBody, TokenHost);
            var accessToken = ReadStringOrEmpty(tokenDocument.RootElement, "access_token");
            if (accessToken.Length == 0)
            {
                throw new GeminiApiException(NoAccessTokenMessage, unauthorized: true);
            }

            return accessToken;
        }
    }

    private string SignServiceAccountAssertion(string clientEmail, string privateKeyPem)
    {
        var now = _clock.UtcNow;
        var issuedAt = now.ToUnixTimeSeconds();
        var expiresAt = issuedAt + 3600;

        const string headerJson = """{"alg":"RS256","typ":"JWT"}""";
        var claimsJson = JsonSerializer.Serialize(new
        {
            iss = clientEmail,
            scope = MonitoringReadScope,
            aud = TokenUrl,
            iat = issuedAt,
            exp = expiresAt,
        });

        var signingInput =
            $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(claimsJson))}";

        byte[] signature;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(privateKeyPem);
            signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> ResolveProjectAsync(GeminiCredential credential, string token, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_projectOverride))
        {
            return _projectOverride;
        }

        if (!string.IsNullOrWhiteSpace(credential.Project))
        {
            return credential.Project;
        }

        return await DiscoverProjectAsync(token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DiscoverProjectAsync(string token, CancellationToken cancellationToken)
    {
        var body = await GetAuthenticatedAsync(new Uri(CloudResourceManagerUrl), token, CloudResourceManagerHost, cancellationToken)
            .ConfigureAwait(false);

        using var document = ParseJsonOrThrow(body, CloudResourceManagerHost);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("projects", out var projects)
            && projects.ValueKind == JsonValueKind.Array)
        {
            foreach (var project in projects.EnumerateArray())
            {
                var projectId = ReadStringOrEmpty(project, "projectId");
                if (projectId.Length > 0)
                {
                    return projectId;
                }
            }
        }

        throw new GeminiApiException(NoActiveProjectMessage);
    }

    // -- Cloud Monitoring ---------------------------------------------------

    private readonly record struct MonitoringPoint(string? StartTime, string? EndTime, long Value);

    private async Task<long> GetSeriesTotalAsync(
        string token, string project, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var totalSeconds = (end - start).TotalSeconds;
        var periodSeconds = totalSeconds > int.MaxValue ? int.MaxValue : Math.Max(60, (int)totalSeconds);

        var points = await QueryMonitoringAsync(token, project, start, end, periodSeconds, cancellationToken).ConfigureAwait(false);

        long total = 0;
        foreach (var point in points)
        {
            total = AddCount(total, point.Value);
        }

        return total;
    }

    private async Task<(UsageHistory History, long Total)> GetHistoryAsync(
        string token, string project, int days, string metric, DateOnly localToday, DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        if (days is < 1 or > MaxHistoryDays)
        {
            throw new GeminiApiException($"History range must be between 1 and {MaxHistoryDays} days.");
        }

        // Days are the user's days. The window starts at local midnight and is converted to UTC for the
        // API, so the 24-hour alignment lands on the boundaries the chart is labelled with.
        var first = localToday.AddDays(-(days - 1));
        var startUtc = LocalDayStartUtc(first, _localTimeZone);

        var points = await QueryMonitoringAsync(token, project, startUtc, endUtc, 86_400, cancellationToken).ConfigureAwait(false);

        var byDay = new Dictionary<DateOnly, long>();
        foreach (var point in points)
        {
            var day = BucketDay(point, 86_400, _localTimeZone);
            if (day is null)
            {
                continue;
            }

            byDay[day.Value] = AddCount(byDay.GetValueOrDefault(day.Value), point.Value);
        }

        var buckets = new List<UsageHistoryBucket>(days);
        for (var offset = 0; offset < days; offset++)
        {
            var day = first.AddDays(offset);
            var value = byDay.GetValueOrDefault(day);
            IReadOnlyDictionary<string, double>? perModel = value > 0
                ? new Dictionary<string, double> { [RequestsSeriesLabel] = value }
                : null;
            buckets.Add(new UsageHistoryBucket(day, perModel));
        }

        long total = 0;
        foreach (var value in byDay.Values)
        {
            total = AddCount(total, value);
        }
        var effectiveMetric = string.IsNullOrWhiteSpace(metric) ? UsageMetric.TotalTokens : metric;
        IReadOnlyList<UsageHistoryBreakdown> byModel = total > 0
            ? [new UsageHistoryBreakdown(RequestsSeriesLabel, total)]
            : [];
        var history = new UsageHistory(
            buckets: buckets,
            series: [RequestsSeriesLabel],
            byModel: byModel,
            byProject: [],
            days: days,
            metric: effectiveMetric,
            projectLabel: "By project");

        return (history, total);
    }

    private async Task<IReadOnlyList<MonitoringPoint>> QueryMonitoringAsync(
        string token, string project, DateTimeOffset start, DateTimeOffset end, int periodSeconds, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Key, string Value)> baseQuery =
        [
            ("filter", MonitoringFilter),
            ("interval.startTime", start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            ("interval.endTime", end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            ("aggregation.alignmentPeriod", periodSeconds.ToString(CultureInfo.InvariantCulture) + "s"),
            ("aggregation.perSeriesAligner", "ALIGN_SUM"),
            ("aggregation.crossSeriesReducer", "REDUCE_SUM"),
        ];

        var points = new List<MonitoringPoint>();
        var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        for (var page = 0; page < MaxMonitoringPages; page++)
        {
            var query = new List<(string Key, string Value)>(baseQuery);
            if (pageToken is not null)
            {
                query.Add(("pageToken", pageToken));
            }

            var uri = BuildUri(string.Format(CultureInfo.InvariantCulture, MonitoringUrlTemplate, Uri.EscapeDataString(project)), query);
            var body = await GetAuthenticatedAsync(uri, token, MonitoringHost, cancellationToken).ConfigureAwait(false);
            using var document = ParseJsonOrThrow(body, MonitoringHost);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new GeminiApiException($"Bad response from {MonitoringHost}. Try again.");
            }

            if ((root.TryGetProperty("executionErrors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                || (root.TryGetProperty("unreachable", out var unreachable) && unreachable.ValueKind == JsonValueKind.Array && unreachable.GetArrayLength() > 0))
            {
                throw new GeminiApiException("Cloud Monitoring returned incomplete results. Try again.");
            }

            if (!root.TryGetProperty("timeSeries", out var series))
            {
                series = default;
            }

            if (series.ValueKind == JsonValueKind.Array)
            {
                foreach (var oneSeries in series.EnumerateArray())
                {
                    if (oneSeries.ValueKind != JsonValueKind.Object
                        || !oneSeries.TryGetProperty("points", out var seriesPoints)
                        || seriesPoints.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var point in seriesPoints.EnumerateArray())
                    {
                        points.Add(new MonitoringPoint(
                            ReadIntervalTimestamp(point, "startTime"), ReadIntervalTimestamp(point, "endTime"), ReadPointValue(point)));
                    }
                }
            }

            pageToken = ReadStringOrEmpty(root, "nextPageToken");
            if (pageToken.Length == 0)
            {
                return points;
            }

            if (!seenPageTokens.Add(pageToken))
            {
                throw new GeminiApiException("Cloud Monitoring returned an invalid pagination cursor.");
            }
        }

        throw new GeminiApiException("Cloud Monitoring returned too many result pages.");
    }

    private static string? ReadIntervalTimestamp(JsonElement point, string field)
    {
        if (point.ValueKind != JsonValueKind.Object
            || !point.TryGetProperty("interval", out var interval)
            || interval.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return interval.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Google's Monitoring API encodes <c>int64Value</c> as a JSON string (proto3 JSON mapping
    /// keeps 64-bit integers out of JSON numbers), so both fields are read leniently as either a string
    /// or a number. Clamped to zero rather than propagating a negative count: a request-count metric is
    /// never legitimately negative, and a validated <see cref="Domain.UsageHistoryBucket"/> would
    /// otherwise throw on one, turning a malformed response into an unhandled exception instead of a
    /// clean, sanitized failure.</summary>
    private static long ReadPointValue(JsonElement point)
    {
        if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (value.TryGetProperty("int64Value", out var intValue) && TryParseFlexibleLong(intValue, out var fromInt))
        {
            return Math.Max(0, fromInt);
        }

        if (value.TryGetProperty("doubleValue", out var doubleValue) && TryParseFlexibleLong(doubleValue, out var fromDouble))
        {
            return Math.Max(0, fromDouble);
        }

        return 0;
    }

    private static bool TryParseFlexibleLong(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromString))
        {
            value = (long)fromString;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var integer))
        {
            value = integer;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var fromNumber)
            && double.IsFinite(fromNumber) && fromNumber >= long.MinValue && fromNumber < 9_223_372_036_854_775_808d)
        {
            value = checked((long)fromNumber);
            return true;
        }

        value = 0;
        return false;
    }

    private static long AddCount(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw new GeminiApiException("Cloud Monitoring returned a request count that is too large.");
        }
    }

    /// <summary>The local day <paramref name="point"/>'s <c>interval.startTime</c> falls on - or, when
    /// only <c>endTime</c> is present (older payloads), <c>endTime</c> minus the alignment period.
    /// Points are labelled with the interval they cover, and for an aligned series that interval ends at
    /// the start of the next one, so reading the day off <c>endTime</c> directly would put every bucket
    /// on the following day. An unparseable/missing timestamp is skipped rather than guessed.</summary>
    private static DateOnly? BucketDay(MonitoringPoint point, int periodSeconds, TimeZoneInfo zone)
    {
        var when = ParseStamp(point.StartTime) ?? ParseStamp(point.EndTime)?.AddSeconds(-periodSeconds);
        if (when is null)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(when.Value, zone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    private static DateTimeOffset? ParseStamp(string? stamp) =>
        !string.IsNullOrEmpty(stamp)
        && DateTimeOffset.TryParse(
            stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    /// <summary>Midnight at the start of <paramref name="day"/> in <paramref name="zone"/>, expressed as
    /// a UTC instant. Built from an unspecified-kind local datetime so the zone's offset actually in
    /// effect on that calendar day is applied - not "now"'s offset carried backwards, which is wrong by
    /// an hour on the day a DST change falls.</summary>
    private static DateTimeOffset LocalDayStartUtc(DateOnly day, TimeZoneInfo zone)
    {
        var localMidnight = day.ToDateTime(TimeOnly.MinValue);
        var offset = zone.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, offset).ToUniversalTime();
    }

    /// <summary>1,284 / 12.9K / 4.2M / 1B - the stat-tile number format, matching the Python baseline's
    /// <c>formatting.compact()</c> so the figures read the same as the app this replaces.</summary>
    private static string FormatCompactNumber(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000_000)
        {
            return FormatSuffixed(value / 1_000_000_000, "B");
        }

        if (magnitude >= 1_000_000)
        {
            return FormatSuffixed(value / 1_000_000, "M");
        }

        if (magnitude >= 10_000)
        {
            return FormatSuffixed(value / 1_000, "K");
        }

        return value.ToString("#,##0", CultureInfo.InvariantCulture);
    }

    private static string FormatSuffixed(double scaled, string suffix)
    {
        var rounded = scaled.ToString("0.0", CultureInfo.InvariantCulture);
        if (rounded.EndsWith(".0", StringComparison.Ordinal))
        {
            rounded = rounded[..^2];
        }

        return rounded + suffix;
    }

    // -- HTTP transport -------------------------------------------------------

    private async Task<string> GetAuthenticatedAsync(Uri uri, string token, string host, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.TryParseAdd(UserAgentValue);
        return await SendAsync(request, host, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PostFormAsync(
        Uri uri, IReadOnlyDictionary<string, string> form, string host, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        request.Headers.UserAgent.TryParseAdd(UserAgentValue);
        return await SendAsync(request, host, cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildUri(string baseUrl, IReadOnlyList<(string Key, string Value)> query)
    {
        var queryString = string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{baseUrl}?{queryString}");
    }

    private async Task<string> SendAsync(HttpRequestMessage request, string host, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        using (request)
        {
            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The linked token fired from the timeout side, not the caller's - a safe error to
                // throw, not a propagated cancellation. Rechecked in case the caller cancelled in the
                // narrow window between the exception and this line.
                cancellationToken.ThrowIfCancellationRequested();
                throw new GeminiApiException($"The request to {host} timed out. Try again.");
            }
            catch (HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new GeminiApiException($"Could not reach {host}. Check your network connection and try again.");
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw MapErrorResponse(response, host);
            }

            try
            {
                return await ReadResponseBodyAsync(response, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new GeminiApiException($"The request to {host} timed out. Try again.");
            }
            catch (GeminiResponseBodyException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new GeminiApiException($"Could not read the response from {host}. Try again.");
            }
        }
    }

    private static GeminiApiException MapErrorResponse(HttpResponseMessage response, string host)
    {
        var statusCode = (int)response.StatusCode;

        if (statusCode is 401 or 403)
        {
            return new GeminiApiException(RejectedMessage, unauthorized: true);
        }

        // No response body is ever echoed here - an arbitrary server-provided message is untrusted.
        return new GeminiApiException($"{host} returned HTTP {statusCode}.");
    }

    /// <summary>Marker for a response-body failure that must become a fixed safe error: never carries
    /// the offending bytes/text, so it is safe to catch broadly at the call site.</summary>
    private sealed class GeminiResponseBodyException : Exception;

    /// <summary>
    /// Reads the response body from a stream with a byte cap enforced independent of
    /// <c>Content-Length</c> (which may be absent or understated), then decodes it with a strict UTF-8
    /// decoder. Mirrors <c>ClaudeLiveQuotaClient</c>/<c>OpenAiLiveQuotaClient</c>'s response reader; kept
    /// as its own small copy rather than a shared helper so this provider's slice does not modify
    /// already-reviewed code in another provider.
    /// </summary>
    private async Task<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
        {
            throw new GeminiResponseBodyException();
        }

        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int bytesRead;
            try
            {
                while ((bytesRead = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + bytesRead > _maxResponseBodyBytes)
                    {
                        throw new GeminiResponseBodyException();
                    }

                    buffer.Write(chunk, 0, bytesRead);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
            {
                throw new GeminiResponseBodyException();
            }

            try
            {
                return StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            catch (DecoderFallbackException)
            {
                throw new GeminiResponseBodyException();
            }
        }
    }

    private static JsonDocument ParseJsonOrThrow(string body, string host)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new GeminiApiException($"Bad response from {host}. Try again.");
        }
    }

    private static string ReadStringOrEmpty(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
