using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Application's <see cref="IProviderQuotaClient"/> port for OpenAI. Resolves the credential in the
/// PAR-008 priority order (<see cref="OpenAiCredentialResolver"/>) and then branches: a Codex ChatGPT
/// OAuth login is read through the isolated App Server (<see cref="ICodexAppServerLauncher"/>,
/// PAR-009); anything else falls back to the organization Admin API for spend/budget/history
/// (PAR-010), for which an ordinary (non-Admin) project key is reported Limited rather than probed
/// with a doomed request. Never falls back from an existing-but-unusable Codex login to the Admin API
/// silently - see <see cref="OpenAiCredentialResolver.DetectAsync"/>.
/// </summary>
public sealed class OpenAiLiveQuotaClient : IProviderQuotaClient
{
    private const string ProviderId = OpenAiCredentialResolver.ProviderId;

    private const string SetupHint =
        "Sign in to Codex with ChatGPT on this machine to see Codex quota percentages, reset times, " +
        "and available daily token totals. Requires Codex CLI or the Codex desktop app. The existing " +
        "login is read-only; open Codex to renew an expired login. A Codex ChatGPT login always takes " +
        "priority over a saved or environment key. When no Codex ChatGPT login is found, an optional " +
        "organization Admin key (sk-admin-…) provides API platform spend; regular project keys " +
        "cannot read API spend.";

    private const string CodexValueNote =
        "Codex quota reported by OpenAI. Daily history contains total tokens only; output tokens, " +
        "model/project breakdowns, and API spend are unavailable.";

    private const string AdminValueNote = "Actual API spend billed by OpenAI.";

    private const string NoQuotaWindowsError = "OpenAI returned no percentage quota windows for this account.";

    private const string CodexExpiredMessage =
        "Codex login expired. Open Codex to refresh the login, then refresh here.";

    private const string TimeoutMessage = "The OpenAI usage request timed out. Try again.";

    private const string TransportErrorMessage =
        "Could not reach api.openai.com. Check your network connection and try again.";

    private const string ResponseBodyErrorMessage = "Could not read the OpenAI usage response. Try again.";

    private const string InvalidResponseMessage = "Bad response from OpenAI. Try again.";

    private const string BaseUrl = "https://api.openai.com";
    private const string UsageCompletionsPath = "/v1/organization/usage/completions";
    private const string CostsPath = "/v1/organization/costs";
    private const string UserAgentValue = "AIUsageMonitor/2.0";

    /// <summary>The Costs/Usage endpoints only support daily buckets, matching the Python baseline and
    /// the UI's largest selectable range (PAR-015: 7/14/30/90 days) - the widest window this client
    /// ever requests, regardless of what a caller passes in.</summary>
    private const int MaxHistoryDays = 90;

    /// <summary>OpenAI's hard per-page cap on daily buckets for both the Usage and Costs endpoints.
    /// A <c>limit</c> above this is rejected, so any window wider than this is paged across multiple
    /// requests instead (<see cref="GetAllBucketsAsync"/>).</summary>
    private const int MaxPageSize = 31;

    /// <summary>Hard ceiling on pages followed for a single bucketed fetch. Independent of the
    /// duplicate-cursor check in <see cref="GetAllBucketsAsync"/> - this guards against a server that
    /// always returns a fresh-looking cursor with <c>has_more: true</c> forever.</summary>
    private const int MaxPages = 10;

    private const int MaxModelSeries = 8;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxSupportedTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private const int DefaultMaxResponseBodyBytes = 1_000_000;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly string _userProfileDirectory;
    private readonly IClock _clock;
    private readonly string? _savedAdminKey;
    private readonly double? _monthlyBudgetUsd;
    private readonly string? _codexHomeOverride;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly ICodexAppServerLauncher _codexLauncher;
    private readonly TimeSpan _timeout;
    private readonly int _maxResponseBodyBytes;
    private readonly TimeZoneInfo _localTimeZone;

    /// <param name="httpClient">Caller-owned; this client sends requests through it but never disposes
    /// it. Used only for the Admin API path - the Codex path never touches HTTP.</param>
    /// <param name="userProfileDirectory">The current user's profile directory, used as the base for
    /// the default Codex CLI login path.</param>
    /// <param name="savedAdminKey">The Admin key saved in this app's own settings, if any - supplied
    /// by the caller exactly like the Python baseline's <c>configure()</c>; this client does not read
    /// or know about a settings store.</param>
    /// <param name="monthlyBudgetUsd">An optional local monthly spend target (not an OpenAI-enforced
    /// limit); when set and positive, "month to date" is shown as a percentage of it.</param>
    /// <param name="codexHomeOverride">The value of <c>CODEX_HOME</c>, if any.</param>
    /// <param name="environmentVariableReader">Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>;
    /// tests inject a fixed map so environment-sourced credentials are resolved deterministically.</param>
    /// <param name="codexLauncher">Defaults to a real <see cref="CodexAppServerLauncher"/>; tests
    /// inject a hand-written fake instead of a real subprocess.</param>
    /// <param name="timeout">Overrides the 15-second production default per Admin API request; must be
    /// positive, finite, and within what <see cref="CancellationTokenSource"/> supports.</param>
    /// <param name="maxResponseBodyBytes">Overrides the 1,000,000-byte production default cap on each
    /// Admin API response body; must be positive.</param>
    /// <param name="localTimeZone">The zone used for "today"/"month start" boundaries and the Codex
    /// history's local-day bucketing. Defaults to <see cref="TimeZoneInfo.Local"/>; tests inject a
    /// fixed zone.</param>
    public OpenAiLiveQuotaClient(
        HttpClient httpClient,
        string userProfileDirectory,
        IClock clock,
        string? savedAdminKey = null,
        double? monthlyBudgetUsd = null,
        string? codexHomeOverride = null,
        Func<string, string?>? environmentVariableReader = null,
        ICodexAppServerLauncher? codexLauncher = null,
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
        _savedAdminKey = savedAdminKey;
        _monthlyBudgetUsd = monthlyBudgetUsd;
        _codexHomeOverride = codexHomeOverride;
        _environmentVariableReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        _codexLauncher = codexLauncher ?? new CodexAppServerLauncher();
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

        var codexAuthPath = OpenAiCodexAuthPathResolver.Resolve(_codexHomeOverride, _userProfileDirectory);
        var resolution = await OpenAiCredentialResolver
            .DetectAsync(_savedAdminKey, _environmentVariableReader, codexAuthPath, _clock, cancellationToken)
            .ConfigureAwait(false);

        return resolution.Credential?.Kind == OpenAiCredentialKind.OAuth
            ? await FetchCodexAsync(resolution, request, cancellationToken).ConfigureAwait(false)
            : await FetchAdminApiAsync(resolution, request, cancellationToken).ConfigureAwait(false);
    }

    // -- Codex path (PAR-009) ---------------------------------------------

    private async Task<ProviderSnapshot> FetchCodexAsync(
        OpenAiResolution resolution, ProviderSnapshotRequest request, CancellationToken cancellationToken)
    {
        var detection = resolution.Detection;
        var credential = resolution.Credential!;
        var configured = detection.State is DetectionState.Connected or DetectionState.Expired;
        var setupHint = detection.Hint.Length > 0 ? detection.Hint : SetupHint;

        if (!configured)
        {
            // Limited: e.g. a Codex login with no account ID. Nothing usable to try.
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId, configured: false, detection: detection, account: detection.Account, setupHint: setupHint);
        }

        if (detection.State == DetectionState.Expired)
        {
            // Never silently fall back to the Admin API here (PAR-008): report the expired login as
            // an actionable, unauthorized error instead.
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId, configured: true, unauthorized: true, error: CodexExpiredMessage,
                detection: detection, account: detection.Account, setupHint: setupHint);
        }

        try
        {
            var result = await _codexLauncher
                .RunAsync(credential, request.IncludeHistory, cancellationToken)
                .ConfigureAwait(false);

            var meters = CodexQuotaMapper.Meters(result.RateLimits);
            var account = detection.Account.Length > 0 ? $"{detection.Account} · Codex" : "Codex (ChatGPT login)";

            UsageHistory? history = null;
            IReadOnlyList<Stat> stats = [];
            if (result.Usage is JsonElement usage)
            {
                var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.UtcNow, _localTimeZone).DateTime);
                (history, stats) = CodexQuotaMapper.History(usage, request.HistoryDays, request.Metric, todayLocal);
            }

            return new ProviderSnapshot(
                ProviderId,
                configured: true,
                meters: meters,
                stats: stats,
                history: history,
                account: account,
                // A session that returned zero usable quota windows is a real failure, even though
                // the App Server call itself succeeded - but history fetched on the same session must
                // still show (PAR-004): the two are reported independently, never one hiding the other.
                error: meters.Count == 0 ? NoQuotaWindowsError : null,
                historyError: result.HistoryError,
                fetchedAt: _clock.UtcNow,
                detection: detection,
                setupHint: setupHint,
                valueNote: CodexValueNote);
        }
        catch (CodexAppServerException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId, configured: true, unauthorized: ex.Unauthorized, error: ex.Message,
                account: detection.Account, detection: detection, setupHint: setupHint);
        }
    }

    // -- Admin API path (PAR-010) ------------------------------------------

    private async Task<ProviderSnapshot> FetchAdminApiAsync(
        OpenAiResolution resolution, ProviderSnapshotRequest request, CancellationToken cancellationToken)
    {
        var detection = resolution.Detection;

        string? key = null;
        if (detection.State == DetectionState.Connected
            && resolution.Credential is { Kind: OpenAiCredentialKind.ApiKey } apiKeyCredential)
        {
            key = apiKeyCredential.Value;
        }

        if (key is null)
        {
            // Includes the ordinary-project-key case: `Connected` never happens for a non-Admin key
            // (OpenAiCredentialResolver marks it Limited), so no request is ever attempted for one -
            // it is reported Limited with the actionable reason instead of a doomed HTTP call.
            cancellationToken.ThrowIfCancellationRequested();
            var account = detection.State == DetectionState.Limited ? detection.Account : string.Empty;
            var setupHintForMissingKey = detection.Hint.Length > 0 ? detection.Hint : SetupHint;
            return new ProviderSnapshot(
                ProviderId, configured: false, detection: detection, account: account,
                setupHint: setupHintForMissingKey, valueNote: AdminValueNote);
        }

        var now = _clock.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        double monthCost;
        double todayCost;
        try
        {
            monthCost = await GetTotalCostAsync(key, monthStart.ToUnixTimeSeconds(), cancellationToken).ConfigureAwait(false);
            todayCost = await GetTotalCostAsync(key, todayStart.ToUnixTimeSeconds(), cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAiApiException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId, configured: true, unauthorized: ex.Unauthorized, error: ex.Message,
                detection: detection, setupHint: SetupHint, valueNote: AdminValueNote);
        }

        var meters = new List<Meter>(2);
        var nextMonthReset = NextMonthStart(now);
        if (_monthlyBudgetUsd is double budget && budget > 0)
        {
            var percent = Math.Min(100.0, monthCost / budget * 100.0);
            meters.Add(new Meter(
                kind: "month_budget",
                key: "month_budget",
                title: "Month to date",
                subtitle: $"of your {OpenAiNumberFormatting.FormatMoney(budget)} target",
                percent: percent,
                serverSeverity: MonthBudgetSeverity(percent),
                resetsAt: nextMonthReset,
                detail: OpenAiNumberFormatting.FormatMoney(monthCost)));
        }
        else
        {
            meters.Add(new Meter(
                kind: "month_spend",
                key: "month_spend",
                title: "Month to date",
                subtitle: "API spend",
                percent: null,
                resetsAt: nextMonthReset,
                detail: OpenAiNumberFormatting.FormatMoney(monthCost)));
        }

        meters.Add(new Meter(
            kind: "today_spend",
            key: "today_spend",
            title: "Today",
            subtitle: "API spend",
            percent: null,
            detail: OpenAiNumberFormatting.FormatMoney(todayCost)));

        UsageHistory? history = null;
        IReadOnlyList<Stat> stats = [];
        string? historyError = null;
        if (request.IncludeHistory)
        {
            try
            {
                (history, stats) = await GetHistoryAsync(key, request.HistoryDays, request.Metric, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OpenAiApiException ex)
            {
                // A failure fetching the range behind the two meters above is a gap in the page, not
                // the service having failed - the meters are already read and real (PAR-004).
                cancellationToken.ThrowIfCancellationRequested();
                historyError = ex.Message;
            }
        }

        var accountLabel = detection.Account.Length > 0
            ? $"{detection.Account} · {detection.SourceLabel}"
            : $"OpenAI organization (API platform) · {detection.SourceLabel}";

        return new ProviderSnapshot(
            ProviderId,
            configured: true,
            meters: meters,
            stats: stats,
            history: history,
            account: accountLabel,
            historyError: historyError,
            fetchedAt: now,
            detection: detection,
            setupHint: SetupHint,
            valueNote: AdminValueNote);
    }

    private static Severity MonthBudgetSeverity(double percent) =>
        percent >= 95 ? Severity.Critical
        : percent >= 85 ? Severity.VeryHigh
        : percent >= 70 ? Severity.High
        : Severity.Normal;

    private static DateTimeOffset NextMonthStart(DateTimeOffset now)
    {
        var (year, month) = now.Month == 12 ? (now.Year + 1, 1) : (now.Year, now.Month + 1);
        return new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Fetches every page of a bucketed Admin API list endpoint (Usage or Costs), following
    /// <c>has_more</c>/<c>next_page</c> until the server reports completion. The caller must already
    /// cap the <c>limit</c> in <paramref name="query"/> at <see cref="MaxPageSize"/>. Bounded to
    /// <see cref="MaxPages"/> iterations, and a cursor repeated across pages aborts the fetch instead of
    /// looping forever - both cases become a sanitized error rather than a silently-incomplete total.
    /// </summary>
    private async Task<List<JsonElement>> GetAllBucketsAsync(
        string key, string path, IReadOnlyList<(string Key, string Value)> query, CancellationToken cancellationToken)
    {
        var buckets = new List<JsonElement>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;

        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var pagedQuery = query;
            if (page is not null)
            {
                pagedQuery = [.. query, ("page", page)];
            }

            var body = await GetAsync(key, path, pagedQuery, cancellationToken).ConfigureAwait(false);

            using var document = ParseJson(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                // Required shape missing - reporting $0/no usage here would look identical to a
                // genuinely quiet period, so this must surface as an error instead.
                throw new OpenAiApiException(InvalidResponseMessage);
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new OpenAiApiException(InvalidResponseMessage);
                }

                // Cloned because `document` is disposed at the end of this iteration, while `buckets`
                // must outlive every page fetched here.
                buckets.Add(item.Clone());
            }

            var hasMore = document.RootElement.TryGetProperty("has_more", out var hasMoreElement)
                && hasMoreElement.ValueKind == JsonValueKind.True;
            if (!hasMore)
            {
                return buckets;
            }

            if (!document.RootElement.TryGetProperty("next_page", out var nextPageElement)
                || nextPageElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(nextPageElement.GetString()))
            {
                // has_more is true but there is no cursor to follow - the range cannot actually be
                // completed, so this must not be reported as if it already were.
                throw new OpenAiApiException(InvalidResponseMessage);
            }

            page = nextPageElement.GetString();
            if (!seenCursors.Add(page!))
            {
                // Loop detection: the server handed back a cursor already followed this fetch.
                throw new OpenAiApiException(InvalidResponseMessage);
            }
        }

        throw new OpenAiApiException(InvalidResponseMessage);
    }

    private async Task<double> GetTotalCostAsync(string key, long startUnixSeconds, CancellationToken cancellationToken)
    {
        var buckets = await GetAllBucketsAsync(
            key,
            CostsPath,
            [
                ("start_time", startUnixSeconds.ToString(CultureInfo.InvariantCulture)),
                ("bucket_width", "1d"),
                ("limit", MaxPageSize.ToString(CultureInfo.InvariantCulture)),
            ],
            cancellationToken).ConfigureAwait(false);

        double total = 0;
        foreach (var bucket in buckets)
        {
            total += SumResultsAmount(bucket);
        }

        return total;
    }

    private async Task<(UsageHistory History, IReadOnlyList<Stat> Stats)> GetHistoryAsync(
        string key, int requestedDays, string metric, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(requestedDays, 1, MaxHistoryDays);
        var pageLimit = Math.Min(days, MaxPageSize);
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _localTimeZone).DateTime);
        var startLocal = todayLocal.AddDays(-(days - 1));
        var startUnixSeconds = new DateTimeOffset(
            startLocal.ToDateTime(TimeOnly.MinValue),
            _localTimeZone.GetUtcOffset(startLocal.ToDateTime(TimeOnly.MinValue))).ToUnixTimeSeconds();
        var startText = startUnixSeconds.ToString(CultureInfo.InvariantCulture);
        var limitText = pageLimit.ToString(CultureInfo.InvariantCulture);

        var usageBuckets = await GetAllBucketsAsync(
            key, UsageCompletionsPath,
            [("start_time", startText), ("bucket_width", "1d"), ("group_by", "model"), ("limit", limitText)],
            cancellationToken).ConfigureAwait(false);
        var costBuckets = await GetAllBucketsAsync(
            key, CostsPath,
            [("start_time", startText), ("bucket_width", "1d"), ("limit", limitText)],
            cancellationToken).ConfigureAwait(false);

        var costByDay = new Dictionary<DateOnly, double>();
        foreach (var bucket in costBuckets)
        {
            var day = BucketDay(bucket, _localTimeZone);
            costByDay[day] = costByDay.GetValueOrDefault(day) + SumResultsAmount(bucket);
        }

        var tokensByDay = new Dictionary<DateOnly, Dictionary<string, TokenCell>>();
        long totalInput = 0, totalOutput = 0, totalCached = 0, totalRequests = 0;

        foreach (var bucket in usageBuckets)
        {
            var day = BucketDay(bucket, _localTimeZone);
            if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                throw new OpenAiApiException(InvalidResponseMessage);
            }

            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object)
                {
                    throw new OpenAiApiException(InvalidResponseMessage);
                }

                var model = ReadOptionalString(result, "model");
                var modelName = string.IsNullOrWhiteSpace(model) ? "unknown" : model;
                var input = ReadNonNegativeLong(result, "input_tokens");
                var output = ReadNonNegativeLong(result, "output_tokens");
                var cached = ReadNonNegativeLong(result, "input_cached_tokens");
                var requests = ReadNonNegativeLong(result, "num_model_requests");

                var perModel = tokensByDay.TryGetValue(day, out var existing)
                    ? existing
                    : tokensByDay[day] = new Dictionary<string, TokenCell>(StringComparer.Ordinal);
                var cell = perModel.GetValueOrDefault(modelName);
                perModel[modelName] = new TokenCell(
                    cell.Input + input, cell.Output + output, cell.Cached + cached, cell.Requests + requests);

                totalInput += input;
                totalOutput += output;
                totalCached += cached;
                totalRequests += requests;
            }
        }

        var totalCost = costByDay.Values.Sum();

        var buckets = new List<UsageHistoryBucket>(days);
        var modelTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var offset = 0; offset < days; offset++)
        {
            var day = startLocal.AddDays(offset);
            var perModel = tokensByDay.GetValueOrDefault(day);
            var dayTokens = perModel is null ? 0L : perModel.Values.Sum(cell => cell.Input + cell.Output);
            var divisor = dayTokens == 0 ? 1.0 : dayTokens;

            var bucketValues = new Dictionary<string, double>(StringComparer.Ordinal);
            if (perModel is not null)
            {
                foreach (var (model, cell) in perModel)
                {
                    var value = metric switch
                    {
                        UsageMetric.OutputTokens => (double)cell.Output,
                        // Costs are not grouped by model, so apportion the day's spend by that
                        // model's share of the day's tokens.
                        UsageMetric.EquivalentValue => Math.Max(0.0, costByDay.GetValueOrDefault(day))
                            * ((cell.Input + cell.Output) / divisor),
                        _ => (double)(cell.Input + cell.Output),
                    };

                    if (value > 0)
                    {
                        bucketValues[model] = value;
                        modelTotals[model] = modelTotals.GetValueOrDefault(model) + value;
                    }
                }
            }

            buckets.Add(new UsageHistoryBucket(day, bucketValues));
        }

        var order = modelTotals.Keys.OrderByDescending(name => modelTotals[name]).ThenBy(name => name, StringComparer.Ordinal).ToList();
        if (order.Count > MaxModelSeries)
        {
            var folded = new HashSet<string>(order.Skip(MaxModelSeries - 1), StringComparer.Ordinal);
            for (var i = 0; i < buckets.Count; i++)
            {
                var bucket = buckets[i];
                if (bucket.PerModel.Count == 0)
                {
                    continue;
                }

                var mutable = new Dictionary<string, double>(bucket.PerModel, StringComparer.Ordinal);
                double spill = 0;
                foreach (var name in folded)
                {
                    if (mutable.Remove(name, out var value))
                    {
                        spill += value;
                    }
                }

                if (spill > 0)
                {
                    mutable["Other"] = spill;
                    buckets[i] = new UsageHistoryBucket(bucket.Day, mutable);
                }
            }

            order = [.. order.Take(MaxModelSeries - 1), "Other"];
        }

        var byModel = modelTotals
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UsageHistoryBreakdown(pair.Key, pair.Value))
            .ToList();

        // Whitespace-only is treated the same as blank - UsageHistory's constructor requires a
        // non-blank metric, and ProviderSnapshotRequest.Metric only guarantees non-null, not non-blank.
        var effectiveMetric = string.IsNullOrWhiteSpace(metric) ? UsageMetric.TotalTokens : metric;
        var history = new UsageHistory(
            buckets: buckets, series: order, byModel: byModel, byProject: [], days: days,
            metric: effectiveMetric, projectLabel: "By project");

        var stats = new List<Stat>
        {
            new("Spend in range", OpenAiNumberFormatting.FormatMoney(totalCost)),
            new("Input tokens", OpenAiNumberFormatting.FormatCompactNumber(totalInput)),
            new("Output tokens", OpenAiNumberFormatting.FormatCompactNumber(totalOutput)),
            new("Cached input", OpenAiNumberFormatting.FormatCompactNumber(totalCached), "billed at the cached rate"),
            new("Requests", OpenAiNumberFormatting.FormatCompactNumber(totalRequests)),
        };

        return (history, stats);
    }

    private readonly record struct TokenCell(long Input, long Output, long Cached, long Requests);

    private static double SumResultsAmount(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiApiException(InvalidResponseMessage);
        }

        double sum = 0;
        foreach (var result in results.EnumerateArray())
        {
            sum += ReadUsdAmount(result);
        }

        return sum;
    }

    /// <summary>
    /// Reads a cost result's <c>amount.value</c>, requiring <c>amount.currency</c> to be <c>"usd"</c>
    /// first. Every total this feeds is formatted and displayed with a "$" prefix
    /// (<see cref="OpenAiNumberFormatting.FormatMoney"/>), so a missing or non-USD currency must fail
    /// closed rather than be summed in and silently mislabeled as USD.
    /// </summary>
    private static double ReadUsdAmount(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("amount", out var amount)
            || amount.ValueKind != JsonValueKind.Object
            || !amount.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var parsed)
            || !double.IsFinite(parsed)
            || !amount.TryGetProperty("currency", out var currency)
            || currency.ValueKind != JsonValueKind.String
            || !string.Equals(currency.GetString(), "usd", StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenAiApiException(InvalidResponseMessage);
        }

        return parsed;
    }

    /// <summary>
    /// The calendar day <paramref name="bucket"/>'s <c>start_time</c> falls on in
    /// <paramref name="localTimeZone"/> - never UTC, matching the Python baseline's
    /// <c>datetime.fromtimestamp(start, utc).astimezone().date()</c>. Bucketing by the UTC date here
    /// instead would shift every day boundary by the zone's offset from UTC.
    /// </summary>
    private static DateOnly BucketDay(JsonElement bucket, TimeZoneInfo localTimeZone)
    {
        if (!bucket.TryGetProperty("start_time", out var startTime)
            || startTime.ValueKind != JsonValueKind.Number
            || !startTime.TryGetDouble(out var seconds)
            || !double.IsFinite(seconds))
        {
            throw new OpenAiApiException(InvalidResponseMessage);
        }

        try
        {
            var instantUtc = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
            var local = TimeZoneInfo.ConvertTime(instantUtc, localTimeZone);
            return DateOnly.FromDateTime(local.DateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new OpenAiApiException(InvalidResponseMessage);
        }
    }

    private static string? ReadOptionalString(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadNonNegativeLong(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
        && parsed >= 0
            ? parsed
            : 0;

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new OpenAiApiException(InvalidResponseMessage);
        }
    }

    // -- HTTP transport -----------------------------------------------------

    private async Task<string> GetAsync(
        string key, string path, IReadOnlyList<(string Key, string Value)> query, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        using (var httpRequest = BuildRequest(key, path, query))
        {
            try
            {
                response = await _httpClient
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OpenAiApiException(TimeoutMessage);
            }
            catch (HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OpenAiApiException(TransportErrorMessage);
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw MapErrorResponse(response);
            }

            try
            {
                return await ReadResponseBodyAsync(response, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OpenAiApiException(TimeoutMessage);
            }
            catch (OpenAiResponseBodyException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OpenAiApiException(ResponseBodyErrorMessage);
            }
        }
    }

    private static HttpRequestMessage BuildRequest(string key, string path, IReadOnlyList<(string Key, string Value)> query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.TryParseAdd(UserAgentValue);
        return request;
    }

    private static Uri BuildUri(string path, IReadOnlyList<(string Key, string Value)> query)
    {
        var queryString = string.Join('&', query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{BaseUrl}{path}?{queryString}");
    }

    private static OpenAiApiException MapErrorResponse(HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;

        if (statusCode is 401 or 403)
        {
            return new OpenAiApiException(
                $"OpenAI rejected the key (HTTP {statusCode}). The Usage API needs an organization " +
                "Admin key (sk-admin-…) with the Usage Dashboard permission.",
                unauthorized: true);
        }

        if (statusCode == 429)
        {
            return new OpenAiApiException("OpenAI is rate-limiting usage requests. Try again shortly.");
        }

        // No response body is ever echoed here - an arbitrary server-provided message is untrusted.
        return new OpenAiApiException($"OpenAI usage request failed (HTTP {statusCode}).");
    }

    /// <summary>Marker for a response-body failure that must become a fixed safe snapshot: never
    /// carries the offending bytes/text, so it is safe to catch broadly at the call site.</summary>
    private sealed class OpenAiResponseBodyException : Exception;

    /// <summary>
    /// Reads the response body from a stream with a byte cap enforced independent of
    /// <c>Content-Length</c>, then decodes it with a strict UTF-8 decoder. Mirrors
    /// <c>ClaudeLiveQuotaClient</c>'s response reader; kept as its own small copy rather than a shared
    /// helper so this provider's slice does not modify Claude's already-reviewed code.
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
            throw new OpenAiResponseBodyException();
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
                        throw new OpenAiResponseBodyException();
                    }

                    buffer.Write(chunk, 0, bytesRead);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
            {
                throw new OpenAiResponseBodyException();
            }

            try
            {
                return StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            catch (DecoderFallbackException)
            {
                throw new OpenAiResponseBodyException();
            }
        }
    }
}
