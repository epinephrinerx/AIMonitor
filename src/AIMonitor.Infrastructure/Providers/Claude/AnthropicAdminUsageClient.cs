using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Outcome of a successful Anthropic Admin API refresh: month and today totals for both cost (USD)
/// and tokens, computed from the two Admin reports (usage and cost) for the current UTC month.
/// </summary>
/// <param name="MonthCostUsd">Month-to-date cost in USD, exact decimal sum of parsed cents amounts.</param>
/// <param name="TodayCostUsd">Today's cost in USD, exact decimal sum of parsed cents amounts.</param>
/// <param name="MonthTokens">Month-to-date token count, <c>checked</c> <see cref="long"/> sum.</param>
/// <param name="TodayTokens">Today's token count, <c>checked</c> <see cref="long"/> sum.</param>
public sealed record AnthropicAdminUsageResult(
    decimal MonthCostUsd,
    decimal TodayCostUsd,
    long MonthTokens,
    long TodayTokens);

/// <summary>
/// Thrown by <see cref="AnthropicAdminUsageClient"/> when the Admin API call fails in a way the UI
/// should surface as "Unavailable". The <see cref="Exception.Message"/> is always a fixed, safe
/// string that never includes the key, headers, response body or the underlying exception text
/// (R-019 E8, E12b).
/// </summary>
public sealed class AnthropicAdminApiException : Exception
{
    public AnthropicAdminApiException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads the Anthropic Admin API's Legacy Usage and Cost reports for the current UTC month. Given
/// a saved Admin key, an injected <see cref="HttpClient"/> and <see cref="IClock"/>, returns a
/// <see cref="AnthropicAdminUsageResult"/> with month and today totals for both cost and tokens.
/// Never leaks the key, headers, response body or exception text into the outcome — every failure
/// becomes an <see cref="AnthropicAdminApiException"/> whose message is a fixed, safe string
/// (R-019 C4, C5, E3–E5, E7).
/// </summary>
public sealed class AnthropicAdminUsageClient
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string UsagePath = "/v1/organizations/usage_report/messages";
    private const string CostPath = "/v1/organizations/cost_report";
    private const string AnthropicVersion = "2026-10-21";
    private const string UserAgentValue = "AIMonitor/2.0";

    private const int MaxPageSize = 31;
    private const int MaxPages = 10;
    private const int DefaultMaxResponseBodyBytes = 1_000_000;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxSupportedTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Returned when the stored Admin key is rejected by the API.</summary>
    public const string UnauthorizedMessage =
        "Admin key rejected — use an organization Admin key (sk-ant-admin…)";

    /// <summary>Returned when the Admin API is not available (e.g. an individual account).</summary>
    public const string NotFoundMessage =
        "Admin API not available for this key — check that it is an organization Admin key with admin access";

    /// <summary>Returned when the API is rate-limiting requests.</summary>
    public const string RateLimitMessage = "Rate limited — try again later";

    /// <summary>Returned when the Admin request exceeds the overall deadline.</summary>
    public const string TimeoutMessage = "The Anthropic Admin API request timed out. Try again.";

    /// <summary>Returned when the API host is unreachable.</summary>
    public const string TransportErrorMessage =
        "Could not reach api.anthropic.com. Check your network connection and try again.";

    /// <summary>Returned when the response body could not be read.</summary>
    public const string ResponseBodyErrorMessage =
        "Could not read the Anthropic Admin API response. Try again.";

    /// <summary>Returned when the response body is syntactically or semantically unusable.</summary>
    public const string ParseErrorMessage = "Bad response from Anthropic Admin API. Try again.";

    private readonly HttpClient _httpClient;
    private readonly string _adminKey;
    private readonly IClock _clock;
    private readonly TimeSpan _timeout;
    private readonly int _maxResponseBodyBytes;

    /// <param name="httpClient">Caller-owned; this client sends requests through it but never disposes it.</param>
    /// <param name="adminKey">The saved Admin API key. Must be non-empty and non-whitespace.</param>
    /// <param name="clock">Used for "now" and the UTC month start.</param>
    /// <param name="timeout">Overrides the 20-second production default; must be positive, finite,
    /// and within what <see cref="CancellationTokenSource"/> supports.</param>
    /// <param name="maxResponseBodyBytes">Overrides the 1,000,000-byte production default cap on
    /// each response page; must be positive.</param>
    public AnthropicAdminUsageClient(
        HttpClient httpClient,
        string adminKey,
        IClock clock,
        TimeSpan? timeout = null,
        int? maxResponseBodyBytes = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminKey);
        ArgumentNullException.ThrowIfNull(clock);

        if (maxResponseBodyBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResponseBodyBytes), maxResponseBodyBytes, "Response body cap must be positive.");
        }

        _httpClient = httpClient;
        _adminKey = adminKey;
        _clock = clock;
        _timeout = ValidateTimeout(timeout ?? DefaultTimeout);
        _maxResponseBodyBytes = maxResponseBodyBytes ?? DefaultMaxResponseBodyBytes;
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

    /// <summary>
    /// Fetches the usage and cost reports for the current UTC month (R-019 E3) and returns their
    /// aggregated totals. Both reports are paged (up to <see cref="MaxPages"/> pages each) and
    /// fetched concurrently under a single overall <see cref="_timeout"/> deadline.
    /// </summary>
    /// <exception cref="AnthropicAdminApiException">Thrown for every expected failure (auth, 404,
    /// rate-limit, transport, parse, timeout). Caller cancellation propagates as
    /// <see cref="OperationCanceledException"/> and is never wrapped (R-019 E7).</exception>
    public async Task<AnthropicAdminUsageResult> GetUsageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nowUtc = _clock.UtcNow.UtcDateTime;
        var monthStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var todayUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        UsageReport usageReport;
        CostReport costReport;
        try
        {
            var usageTask = FetchUsageReportAsync(monthStartUtc, nowUtc, todayUtc, linkedCts.Token);
            var costTask = FetchCostReportAsync(monthStartUtc, nowUtc, todayUtc, linkedCts.Token);
            await Task.WhenAll(usageTask, costTask).ConfigureAwait(false);
            usageReport = usageTask.Result;
            costReport = costTask.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AnthropicAdminApiException(TimeoutMessage);
        }
        catch (HttpRequestException)
        {
            // Ensure the sibling stops too (R-019 E12d).
            linkedCts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new AnthropicAdminApiException(TransportErrorMessage);
        }
        catch (AnthropicAdminApiException)
        {
            // One report failed: cancel the sibling so it stops making network calls (R-019 E12d).
            linkedCts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            linkedCts.Cancel();
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        return new AnthropicAdminUsageResult(
            costReport.MonthCost,
            costReport.TodayCost,
            usageReport.MonthTokens,
            usageReport.TodayTokens);
    }

    private sealed record UsageReport(long MonthTokens, long TodayTokens);
    private sealed record CostReport(decimal MonthCost, decimal TodayCost);

    private async Task<UsageReport> FetchUsageReportAsync(
        DateTime monthStartUtc, DateTime nowUtc, DateTime todayUtc, CancellationToken cancellationToken)
    {
        var startingAt = monthStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var endingAt = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        long monthTokens = 0;
        long todayTokens = 0;
        string? page = null;
        var seenCursors = new HashSet<string?>(StringComparer.Ordinal) { null };

        for (var i = 0; i < MaxPages; i++)
        {
            var url = BuildUsageUrl(startingAt, endingAt, page);
            using var request = BuildRequest(url);

            string body;
            using (var response = await SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            }

            var (bucketTokens, hasMore, nextPage) = ParseUsagePage(body);

            checked
            {
                foreach (var (dayUtc, tokens) in bucketTokens)
                {
                    monthTokens += tokens;
                    if (dayUtc == todayUtc)
                    {
                        todayTokens += tokens;
                    }
                }
            }

            if (!hasMore)
            {
                return new UsageReport(monthTokens, todayTokens);
            }

            if (!seenCursors.Add(nextPage))
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            page = nextPage;
        }

        throw new AnthropicAdminApiException(ParseErrorMessage);
    }

    private async Task<CostReport> FetchCostReportAsync(
        DateTime monthStartUtc, DateTime nowUtc, DateTime todayUtc, CancellationToken cancellationToken)
    {
        var startingAt = monthStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var endingAt = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        decimal monthCost = 0m;
        decimal todayCost = 0m;
        string? page = null;
        var seenCursors = new HashSet<string?>(StringComparer.Ordinal) { null };

        for (var i = 0; i < MaxPages; i++)
        {
            var url = BuildCostUrl(startingAt, endingAt, page);
            using var request = BuildRequest(url);

            string body;
            using (var response = await SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            }

            var (bucketCosts, hasMore, nextPage) = ParseCostPage(body);

            foreach (var (dayUtc, amountUsd) in bucketCosts)
            {
                monthCost += amountUsd;
                if (dayUtc == todayUtc)
                {
                    todayCost += amountUsd;
                }
            }

            if (!hasMore)
            {
                return new CostReport(monthCost, todayCost);
            }

            if (!seenCursors.Add(nextPage))
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            page = nextPage;
        }

        throw new AnthropicAdminApiException(ParseErrorMessage);
    }

    private static string BuildUsageUrl(string startingAt, string endingAt, string? page)
    {
        var sb = new StringBuilder(BaseUrl.Length + UsagePath.Length + 256);
        sb.Append(BaseUrl);
        sb.Append(UsagePath);
        sb.Append("?starting_at=");
        sb.Append(Uri.EscapeDataString(startingAt));
        sb.Append("&ending_at=");
        sb.Append(Uri.EscapeDataString(endingAt));
        sb.Append("&bucket_width=1d");
        sb.Append("&group_by%5B%5D=model");
        sb.Append("&limit=");
        sb.Append(MaxPageSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(page))
        {
            sb.Append("&page=");
            sb.Append(Uri.EscapeDataString(page));
        }

        return sb.ToString();
    }

    private static string BuildCostUrl(string startingAt, string endingAt, string? page)
    {
        var sb = new StringBuilder(BaseUrl.Length + CostPath.Length + 256);
        sb.Append(BaseUrl);
        sb.Append(CostPath);
        sb.Append("?starting_at=");
        sb.Append(Uri.EscapeDataString(startingAt));
        sb.Append("&ending_at=");
        sb.Append(Uri.EscapeDataString(endingAt));
        sb.Append("&bucket_width=1d");
        sb.Append("&limit=");
        sb.Append(MaxPageSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(page))
        {
            sb.Append("&page=");
            sb.Append(Uri.EscapeDataString(page));
        }

        return sb.ToString();
    }

    private HttpRequestMessage BuildRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", _adminKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.TryParseAdd(UserAgentValue);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();

            if (statusCode is 401 or 403)
            {
                throw new AnthropicAdminApiException(UnauthorizedMessage);
            }

            if (statusCode == 404)
            {
                throw new AnthropicAdminApiException(NotFoundMessage);
            }

            if (statusCode == 429)
            {
                throw new AnthropicAdminApiException(RateLimitMessage);
            }

            throw new AnthropicAdminApiException($"Anthropic Admin API request failed (HTTP {statusCode}).");
        }

        return response;
    }

    /// <summary>
    /// Reads the response body from a stream with a byte cap enforced independent of
    /// <c>Content-Length</c>, then decodes it with a strict UTF-8 decoder. Every expected failure
    /// becomes an <see cref="AnthropicAdminApiException"/> whose message is a fixed safe string.
    /// </summary>
    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw new AnthropicAdminApiException(ResponseBodyErrorMessage);
        }
        catch (Exception)
        {
            throw new AnthropicAdminApiException(ResponseBodyErrorMessage);
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
                        throw new AnthropicAdminApiException(ResponseBodyErrorMessage);
                    }

                    buffer.Write(chunk, 0, bytesRead);
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                throw new AnthropicAdminApiException(ResponseBodyErrorMessage);
            }
            catch (Exception)
            {
                throw new AnthropicAdminApiException(ResponseBodyErrorMessage);
            }

            try
            {
                return StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            catch (DecoderFallbackException)
            {
                throw new AnthropicAdminApiException(ResponseBodyErrorMessage);
            }
        }
    }

    /// <summary>
    /// Parses a usage report page. Returns the per-bucket (day, token sum) pairs, whether the API
    /// indicated there are more pages, and the cursor for the next page. A missing or unparsable
    /// envelope, non-numeric token fields, or a negative sum become <see cref="AnthropicAdminApiException"/>
    /// (R-019 E4, E5, E12c).
    /// </summary>
    private static (IReadOnlyList<(DateTime DayUtc, long Tokens)> Buckets, bool HasMore, string? NextPage) ParseUsagePage(string body)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (!root.TryGetProperty("has_more", out var hasMoreElement)
            || (hasMoreElement.ValueKind != JsonValueKind.True && hasMoreElement.ValueKind != JsonValueKind.False))
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }
        var hasMore = hasMoreElement.ValueKind == JsonValueKind.True;

        string? nextPage = null;
        if (root.TryGetProperty("next_page", out var nextPageElement))
        {
            if (nextPageElement.ValueKind == JsonValueKind.String)
            {
                nextPage = nextPageElement.GetString();
            }
            else if (nextPageElement.ValueKind != JsonValueKind.Null)
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }
        }

        var buckets = new List<(DateTime DayUtc, long Tokens)>();

        foreach (var bucket in dataElement.EnumerateArray())
        {
            if (!bucket.TryGetProperty("starting_at", out var startingAtElement)
                || startingAtElement.ValueKind != JsonValueKind.String)
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            if (!DateTimeOffset.TryParse(
                startingAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var startingAt))
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            var dayUtc = startingAt.UtcDateTime.Date;

            long bucketTokens = 0;
            if (!bucket.TryGetProperty("results", out var resultsElement)
                || resultsElement.ValueKind != JsonValueKind.Array)
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            foreach (var result in resultsElement.EnumerateArray())
            {
                bucketTokens += ReadTokenField(result, "uncached_input_tokens");
                bucketTokens += ReadTokenField(result, "cache_read_input_tokens");
                bucketTokens += ReadNestedTokenField(result, "cache_creation", "ephemeral_5m_input_tokens");
                bucketTokens += ReadNestedTokenField(result, "cache_creation", "ephemeral_1h_input_tokens");
                bucketTokens += ReadTokenField(result, "output_tokens");
            }

            buckets.Add((dayUtc, bucketTokens));
        }

        return (buckets, hasMore, nextPage);
    }

    private static long ReadTokenField(JsonElement element, string fieldName)
    {
        if (!element.TryGetProperty(fieldName, out var fieldElement))
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (fieldElement.ValueKind == JsonValueKind.Null)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (fieldElement.ValueKind == JsonValueKind.Number && fieldElement.TryGetInt64(out var value))
        {
            if (value < 0)
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            return value;
        }

        throw new AnthropicAdminApiException(ParseErrorMessage);
    }

    private static long ReadNestedTokenField(JsonElement element, string parentField, string childField)
    {
        if (!element.TryGetProperty(parentField, out var parentElement)
            || parentElement.ValueKind != JsonValueKind.Object)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        return ReadTokenField(parentElement, childField);
    }

    /// <summary>
    /// Parses a cost report page. Returns the per-bucket (day, USD amount) pairs, whether the API
    /// indicated there are more pages, and the cursor for the next page. Negative, non-USD or
    /// unparsable amounts become <see cref="AnthropicAdminApiException"/> (R-019 E4, E12c).
    /// </summary>
    private static (IReadOnlyList<(DateTime DayUtc, decimal AmountUsd)> Buckets, bool HasMore, string? NextPage) ParseCostPage(string body)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (!root.TryGetProperty("has_more", out var hasMoreElement)
            || (hasMoreElement.ValueKind != JsonValueKind.True && hasMoreElement.ValueKind != JsonValueKind.False))
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }
        var hasMore = hasMoreElement.ValueKind == JsonValueKind.True;

        string? nextPage = null;
        if (root.TryGetProperty("next_page", out var nextPageElement))
        {
            if (nextPageElement.ValueKind == JsonValueKind.String)
            {
                nextPage = nextPageElement.GetString();
            }
            else if (nextPageElement.ValueKind != JsonValueKind.Null)
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }
        }

        var buckets = new List<(DateTime DayUtc, decimal AmountUsd)>();

        foreach (var bucket in dataElement.EnumerateArray())
        {
            if (!bucket.TryGetProperty("starting_at", out var startingAtElement)
                || startingAtElement.ValueKind != JsonValueKind.String)
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            if (!DateTimeOffset.TryParse(
                startingAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var startingAt))
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            var dayUtc = startingAt.UtcDateTime.Date;

            decimal bucketCost = 0m;
            if (!bucket.TryGetProperty("results", out var resultsElement)
                || resultsElement.ValueKind != JsonValueKind.Array)
            {
                throw new AnthropicAdminApiException(ParseErrorMessage);
            }

            foreach (var result in resultsElement.EnumerateArray())
            {
                bucketCost += ReadCostAmount(result);
            }

            buckets.Add((dayUtc, bucketCost));
        }

        return (buckets, hasMore, nextPage);
    }

    /// <summary>
    /// Reads a single cost result's <c>amount</c> field — a decimal cents string in invariant
    /// culture — and converts it to USD. Non-USD <c>currency</c>, negative amounts, or unparsable
    /// strings become <see cref="AnthropicAdminApiException"/> (R-019 E4, E12c). The amount is
    /// parsed with <see cref="NumberStyles.AllowDecimalPoint"/> only — no exponent, whitespace or
    /// sign allowed.
    /// </summary>
    private static decimal ReadCostAmount(JsonElement result)
    {
        if (!result.TryGetProperty("currency", out var currencyElement)
            || currencyElement.ValueKind != JsonValueKind.String)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        var currency = currencyElement.GetString();
        if (!string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (!result.TryGetProperty("amount", out var amountElement)
            || amountElement.ValueKind != JsonValueKind.String)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        var amountString = amountElement.GetString();
        if (string.IsNullOrEmpty(amountString))
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (!decimal.TryParse(amountString, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var cents))
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        if (cents < 0m)
        {
            throw new AnthropicAdminApiException(ParseErrorMessage);
        }

        return cents / 100m;
    }
}
