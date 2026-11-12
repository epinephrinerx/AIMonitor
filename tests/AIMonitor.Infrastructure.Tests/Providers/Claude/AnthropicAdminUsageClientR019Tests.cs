using System.Globalization;
using System.Net;
using AIMonitor.Application.Time;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// R-019 (Docs/SPEC_R019.md): C4/C5 + decisions E3/E4/E5/E7/E8 for the
/// <see cref="AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient"/>. Fake
/// <see cref="HttpMessageHandler"/> only — no real network, key, clock or culture dependence.
///
/// <para><b>Aligned API (E11: names/signatures read from the merged <c>src/</c>; behaviour still comes
/// from the spec — where an oracle and the code disagree, the spec decides and the mismatch is
/// reported, not bent):</b></para>
/// <code>
/// new AnthropicAdminUsageClient(
///     HttpClient httpClient,                  // caller-owned, never disposed (house rule)
///     string adminKey,                        // E11: the key goes to the constructor
///     IClock clock,                           // E3: UTC month/today boundaries from clock.UtcNow
///     TimeSpan? timeout = null,               // E7: ONE overall deadline for all Admin requests,
///                                             //     default 20 s (E7; C4's 15 s is superseded)
///     int? maxResponseBodyBytes = null)       // E7: per-page cap, default 1 000 000
/// Task&lt;AnthropicAdminUsageResult&gt; GetUsageAsync(CancellationToken ct);
/// record AnthropicAdminUsageResult(MonthCostUsd, TodayCostUsd, MonthTokens, TodayTokens)
///     — read via the named members; the positional order is (month cost, today cost, month tokens,
///     today tokens).
/// AnthropicAdminApiException — every C5/E4/E5/E7 failure, fixed safe Message;
///     E12b: never carries an inner exception; caller cancellation is never converted (E7).
/// </code>
///
/// <para>E12 additions pinned below: (b) no inner exception on any failure path; (c) every listed
/// malformed shape → Unavailable (missing token field, non-object cache_creation, non-array results,
/// non-Boolean has_more, non-string next_page, currency not "USD", amount with exponent/whitespace/
/// sign; every token addition checked; a cursor seen before in the same report → Unavailable);
/// (d) cancelled caller token after an Admin failure → OperationCanceledException, and one report
/// failing cancels its sibling's in-flight request; (e) starting_at/ending_at sent as
/// "yyyy-MM-ddTHH:mm:ssZ". C5/E9 error texts are pinned verbatim from the spec.</para>
///
/// <para>E13 additions pinned below (fix round 2, from the E13 text — expected values from the spec,
/// not from code): (a) one report failing cancels the shared deadline source AT ONCE — with a long
/// client timeout the sibling request observes cancellation and GetUsageAsync returns long before
/// the deadline; (b) has_more true with an empty-string "" cursor → Unavailable before any further
/// request (request counts pinned), same for null, and the next_page property is REQUIRED on every
/// page — absent → Unavailable, for usage and for cost alike; (c) any exception while the caller
/// token is cancelled surfaces as OperationCanceledException with no sentinel anywhere, and JSON
/// nodes that are not objects (root, bucket, result) → AnthropicAdminApiException, never
/// InvalidOperationException escaping to the caller; (d) currency compared ordinal against "USD"
/// (lowercase "usd" → Unavailable) and bucket starting_at/ending_at must carry Z or an offset
/// (without one → Unavailable, usage and cost); E7 body-read cancellation — a response stream whose
/// ReadAsync blocks until the token fires, for the caller-cancels and the deadline leg. E13g: the
/// public DefaultTimeout (20 s) / DefaultMaxResponseBodyBytes (1,000,000) are pinned, and with the
/// production cap a body of exactly 1,000,000 bytes is accepted while 1,000,001 is Unavailable.
/// The sibling-cancel tests order their handlers with TaskCompletionSources (the failing report
/// answers only once the sibling is really blocked), so no test depends on thread timing.</para>
/// </summary>
[Trait("Category", "Contract")]
public sealed class AnthropicAdminUsageClientR019Tests
{
    // ---- Spec-pinned constants -------------------------------------------------------------

    // C5: 401/403 message, verbatim.
    private const string KeyRejectedMessage =
        "Admin key rejected — use an organization Admin key (sk-ant-admin…)";

    // E9 (overrides C5's 404 row), verbatim.
    private const string AdminApiNotAvailableMessage =
        "Admin API not available for this key — check that it is an organization Admin key with admin access";

    // C5: 429 message, verbatim.
    private const string RateLimitedMessage = "Rate limited — try again later";

    // E8 sentinel: must never appear in any exception text (or anywhere else). Shaped like the real
    // sk-ant-admin prefix so the sweep also covers prefix leakage.
    private const string KeySentinel = "sk-an…6c2e";

    private const string BodyEchoMarker = "BODY-ECHO-MARKER-5f1b";

    // C4 clock: 2026-11-15 08:00 UTC. E3: month = first day of current UTC month 00:00Z → now.
    private static readonly DateTimeOffset NowMidMonthUtc = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
    private const string MonthStartIso = "2026-11-01T00:00:00Z";
    private const string NowIso = "2026-11-15T08:00:00Z";

    // ---- Fixtures (sanitized synthetic payloads, no secrets/PII) ---------------------------

    // Usage: one bucket (today), all five token fields distinct: 7+11+13+17+19 = 67.
    private const string UsageTodayOkSinglePage =
        """
        {"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"claude-sonnet-4-5","uncached_input_tokens":7,"cache_read_input_tokens":11,"cache_creation":{"ephemeral_5m_input_tokens":13,"ephemeral_1h_input_tokens":17},"output_tokens":19}]}],"has_more":false,"next_page":null}
        """;

    // Usage page 1: day 2026-11-13 with two models —
    //   model A: 1000+2000+3000+4000+5000 = 15000
    //   model B:  100+ 200+ 300+ 400+ 500 =  1500  → day total 16500;
    //   plus an EMPTY bucket for day 2026-11-14 (contributes 0, per the Facts: empty buckets have empty
    //   results). has_more: true, next_page: "cursor-1".
    private const string UsagePageOne =
        """
        {"data":[{"starting_at":"2026-11-13T00:00:00Z","ending_at":"2026-11-14T00:00:00Z","results":[{"model":"claude-sonnet-4-5","uncached_input_tokens":1000,"cache_read_input_tokens":2000,"cache_creation":{"ephemeral_5m_input_tokens":3000,"ephemeral_1h_input_tokens":4000},"output_tokens":5000},{"model":"claude-opus-4-1","uncached_input_tokens":100,"cache_read_input_tokens":200,"cache_creation":{"ephemeral_5m_input_tokens":300,"ephemeral_1h_input_tokens":400},"output_tokens":500}]},{"starting_at":"2026-11-14T00:00:00Z","ending_at":"2026-11-15T00:00:00Z","results":[]}],"has_more":true,"next_page":"cursor-1"}
        """;

    // Usage page 2 (requested with page=cursor-1): today's bucket 7+11+13+17+19 = 67.
    // Hand-computed oracle: month = 16500 + 0 + 67 = 16567; today = 67.
    private const string UsagePageTwo =
        """
        {"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"claude-haiku-4-5","uncached_input_tokens":7,"cache_read_input_tokens":11,"cache_creation":{"ephemeral_5m_input_tokens":13,"ephemeral_1h_input_tokens":17},"output_tokens":19}]}],"has_more":false,"next_page":null}
        """;

    // Cost: yesterday "100.00" cents + today "123.45" cents, both USD, single page.
    // Hand-computed oracle: today = 123.45 / 100 = 1.2345m; month = 1.2345m + 1.0000m = 2.2345m.
    private const string CostTodayAndYesterday =
        """
        {"data":[{"starting_at":"2026-11-14T00:00:00Z","ending_at":"2026-11-15T00:00:00Z","results":[{"amount":"100.00","currency":"USD"}]},{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"USD"}]}],"has_more":false,"next_page":null}
        """;

    // Cost with a third bucket of "0.01" cents on day 12 — exact-cent sum (E4):
    // "0.01"→$0.0001 + "100.00"→$1.0000 + "123.45"→$1.2345 → month = $2.2346, today = $1.2345.
    private const string CostWithOneCentBucket =
        """
        {"data":[{"starting_at":"2026-11-12T00:00:00Z","ending_at":"2026-11-13T00:00:00Z","results":[{"amount":"0.01","currency":"USD"}]},{"starting_at":"2026-11-14T00:00:00Z","ending_at":"2026-11-15T00:00:00Z","results":[{"amount":"100.00","currency":"USD"}]},{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"USD"}]}],"has_more":false,"next_page":null}
        """;

    // E13b: valid single-bucket pages whose cursor field is varied by a builder — the same
    // well-formed data (usage today = 1+1+0+0+1 = 3; cost today = "123.45" cents → 1.2345m) so a
    // client that WRONGLY accepts a bad/absent cursor completes with a (possibly double-counted)
    // total instead of Unavailable, and the request-count assertions below decide between
    // "stopped at page 1" and "went on".
    private const string UsageTodayBucketValid =
        """{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}""";

    private const string CostTodayBucketValid =
        """{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"USD"}]}""";

    /// <summary>Usage envelope with <see cref="UsageTodayBucketValid"/> and a chosen cursor field.
    /// Passing <c>null</c> for <paramref name="nextPageJson"/> omits the next_page property
    /// entirely (E13b: the property is required on every page).</summary>
    private static string UsagePageWithCursorField(string? nextPageJson, bool hasMore = true) =>
        nextPageJson is null
            ? $$"""{"data":[{{UsageTodayBucketValid}}],"has_more":{{(hasMore ? "true" : "false")}}}"""
            : $$"""{"data":[{{UsageTodayBucketValid}}],"has_more":{{(hasMore ? "true" : "false")}},"next_page":{{nextPageJson}}}""";

    /// <summary>Cost envelope with <see cref="CostTodayBucketValid"/> and a chosen cursor field.</summary>
    private static string CostPageWithCursorField(string? nextPageJson, bool hasMore = true) =>
        nextPageJson is null
            ? $$"""{"data":[{{CostTodayBucketValid}}],"has_more":{{(hasMore ? "true" : "false")}}}"""
            : $$"""{"data":[{{CostTodayBucketValid}}],"has_more":{{(hasMore ? "true" : "false")}},"next_page":{{nextPageJson}}}""";

    // ---- Shared helpers ----------------------------------------------------------------------

    private static HttpResponseMessage Success(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body) };

    /// <summary>Hand-written fake transport for the two Admin reports (never touches the network):
    /// routes by path and records every request per report kind, so the two concurrently-fetched
    /// reports stay individually assertable (the last-completed request would otherwise be a race).
    /// Each responder is called with that report's own zero-based page index.</summary>
    private sealed class RoutingHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> usageResponder,
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> costResponder) : HttpMessageHandler
    {
        private int _usageIndex;
        private int _costIndex;

        public List<HttpRequestMessage> UsageRequests { get; } = [];

        public List<HttpRequestMessage> CostRequests { get; } = [];

        public int CallCount => UsageRequests.Count + CostRequests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Requests for one report are strictly sequential (one fetch task per report), so the
            // per-report index is race-free even though usage and cost run concurrently.
            if (request.RequestUri!.AbsolutePath.Contains("cost_report", StringComparison.Ordinal))
            {
                CostRequests.Add(request);
                return await costResponder(request, _costIndex++, cancellationToken).ConfigureAwait(false);
            }

            UsageRequests.Add(request);
            return await usageResponder(request, _usageIndex++, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Routes requests by path: each responder is called with its own report's page index so
    /// pagination fixtures can hand out page 1, page 2, ... in order.</summary>
    private static RoutingHandler Respond(
        Func<HttpRequestMessage, int, HttpResponseMessage> usageResponder,
        Func<HttpRequestMessage, int, HttpResponseMessage> costResponder) =>
        new(
            (request, index, _) => Task.FromResult(usageResponder(request, index)),
            (request, index, _) => Task.FromResult(costResponder(request, index)));

    private static RoutingHandler RespondOk() =>
        Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(CostTodayAndYesterday));

    /// <summary>E11 alignment: the key goes to the constructor; the call is GetUsageAsync(ct).</summary>
    private static AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient CreateClient(
        HttpMessageHandler handler,
        IClock? clock = null,
        TimeSpan? timeout = null,
        int? maxResponseBodyBytes = null,
        string adminKey = KeySentinel) =>
        new(
            new HttpClient(handler),
            adminKey,
            clock ?? new FakeClock(NowMidMonthUtc),
            timeout,
            maxResponseBodyBytes);

    private static Dictionary<string, string> QueryOf(HttpRequestMessage request) =>
        request.RequestUri!
            .Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                kv => Uri.UnescapeDataString(kv[0]),
                kv => Uri.UnescapeDataString(kv.Length > 1 ? kv[1] : string.Empty),
                StringComparer.Ordinal);

    /// <summary>Builds a usage page whose first bucket is today with token sum 3
    /// (1+1+0+0+1) and whose cursor is <c>cursor-{index}</c>.</summary>
    private static string UsagePageWithCursor(int index, bool hasMore)
    {
        var bucket = index == 0
            ? """{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}"""
            : """{"starting_at":"2026-11-14T00:00:00Z","ending_at":"2026-11-15T00:00:00Z","results":[]}""";
        var nextPage = hasMore ? $"\"cursor-{index}\"" : "null";
        return $$"""{"data":[{{bucket}}],"has_more":{{(hasMore ? "true" : "false")}},"next_page":{{nextPage}}}""";
    }

    // ---- C4: request shape --------------------------------------------------------------------

    [Fact]
    public async Task GetUsageAsync_UsageRequest_UrlQueryAndHeadersMatchSpec()
    {
        var handler = RespondOk();
        var client = CreateClient(handler);

        await client.GetUsageAsync(CancellationToken.None);

        var request = Assert.Single(handler.UsageRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
        Assert.Equal("/v1/organizations/usage_report/messages", request.RequestUri.AbsolutePath);

        // C4 + E3: starting_at = first day of current UTC month at 00:00Z, ending_at = clock now.
        // E12e: the literal "yyyy-MM-ddTHH:mm:ssZ" form, Gregorian, never local.
        // E5: limit=31 on both reports. C4: bucket_width=1d and group_by[]=model.
        var query = QueryOf(request);
        Assert.Equal(MonthStartIso, query["starting_at"]);
        Assert.Equal(NowIso, query["ending_at"]);
        Assert.Equal("1d", query["bucket_width"]);
        Assert.Equal("model", query["group_by[]"]);
        Assert.Equal("31", query["limit"]);
        Assert.False(query.ContainsKey("page")); // first page carries no page parameter (C4)

        // C4 headers.
        Assert.Equal(KeySentinel, request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2026-10-21", request.Headers.GetValues("anthropic-version").Single());
        Assert.StartsWith("AIMonitor/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUsageAsync_CostRequest_UrlQueryHeadersMatchNoGroupBy()
    {
        var handler = RespondOk();
        var client = CreateClient(handler);

        await client.GetUsageAsync(CancellationToken.None);

        // E12d: both reports run concurrently; the routing fake names the cost request exactly.
        var cost = Assert.Single(handler.CostRequests);
        Assert.Equal("/v1/organizations/cost_report", cost.RequestUri!.AbsolutePath);

        // C4: cost takes starting_at/ending_at/bucket_width=1d only (no group_by) + E5 limit=31.
        var query = QueryOf(cost);
        Assert.Equal(MonthStartIso, query["starting_at"]);
        Assert.Equal(NowIso, query["ending_at"]);
        Assert.Equal("1d", query["bucket_width"]);
        Assert.Equal("31", query["limit"]);
        Assert.False(query.ContainsKey("group_by[]"));
        Assert.False(query.ContainsKey("page"));

        // C4 headers on the cost request too.
        Assert.Equal(KeySentinel, cost.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2026-10-21", cost.Headers.GetValues("anthropic-version").Single());
        Assert.StartsWith("AIMonitor/", cost.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    // ---- C4/E3/E5: two-page sums with distinct fields ----------------------------------------

    [Fact]
    public async Task GetUsageAsync_TwoPageUsage_SumsAllFiveTokenFieldsHandComputed()
    {
        var handler = Respond(
            (_, index) => Success(index == 0 ? UsagePageOne : UsagePageTwo),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        // Oracles (see fixture comments):
        //   day13 = (1000+2000+3000+4000+5000) + (100+200+300+400+500) = 15000 + 1500 = 16500
        //   day14 = 0 (empty bucket), today = 7+11+13+17+19 = 67
        //   month = 16500 + 0 + 67 = 16567
        Assert.Equal(16567, result.MonthTokens);
        Assert.Equal(67, result.TodayTokens);

        // E5/C4: page 2 is requested with the previous next_page as `page`.
        Assert.Equal(2, handler.UsageRequests.Count);
        Assert.False(QueryOf(handler.UsageRequests[0]).ContainsKey("page"));
        Assert.Equal("cursor-1", QueryOf(handler.UsageRequests[1])["page"]);
    }

    [Fact]
    public async Task GetUsageAsync_CostCentsString_ParsesInvariantExactDecimal()
    {
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(CostWithOneCentBucket));
        var client = CreateClient(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        // E4: decimal string in cents ÷ 100 (InvariantCulture), summed exactly — no float drift:
        //   today  = "123.45" cents                                  → 1.2345m
        //   month  = "0.01" (0.0001m) + "100.00" (1.0000m) + "123.45" (1.2345m) → 2.2346m
        Assert.Equal(1.2345m, result.TodayCostUsd);
        Assert.Equal(2.2346m, result.MonthCostUsd);
    }

    // ---- E3: UTC day/month boundaries -----------------------------------------------------------

    [Fact]
    public async Task GetUsageAsync_AtExactMidnightUtc_TodayIsTheNewDayBucket()
    {
        // Clock lands exactly at 2026-11-16T00:00:00Z: a record bucketed at 23:59Z (day 15's bucket,
        // ending 2026-11-16T00:00:00Z) must NOT count as today; the bucket starting 2026-11-16T00:00:00Z
        // must. (E3: "today = the bucket starting at today's 00:00Z".)
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        const string yesterdayBucket =
            """{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1000,"cache_read_input_tokens":2000,"cache_creation":{"ephemeral_5m_input_tokens":3000,"ephemeral_1h_input_tokens":4000},"output_tokens":5000}]}""";
        const string todayBucket =
            """{"starting_at":"2026-11-16T00:00:00Z","ending_at":"2026-11-17T00:00:00Z","results":[{"model":"m","uncached_input_tokens":100,"cache_read_input_tokens":200,"cache_creation":{"ephemeral_5m_input_tokens":300,"ephemeral_1h_input_tokens":400},"output_tokens":500}]}""";
        var usage = $$"""{"data":[{{yesterdayBucket}},{{todayBucket}}],"has_more":false,"next_page":null}""";
        var handler = Respond((_, _) => Success(usage), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler, clock);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(1500, result.TodayTokens);                 // only the 16th bucket: 100+200+300+400+500
        Assert.Equal(16500, result.MonthTokens);                 // 15000 + 1500
        Assert.Equal(MonthStartIso, QueryOf(handler.UsageRequests[0])["starting_at"]);
    }

    [Fact]
    public async Task GetUsageAsync_At2359Utc_TodayIsStillThatUtcDayBucket()
    {
        // Clock at 2026-11-15T23:59:59Z — the last record of the UTC day: the bucket starting
        // 2026-11-15T00:00:00Z is still today (E3: UTC, never local).
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 23, 59, 59, TimeSpan.Zero));
        var handler = RespondOk();
        var client = CreateClient(handler, clock);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(67, result.TodayTokens);
        Assert.Equal(67, result.MonthTokens);
        Assert.Equal("2026-11-15T23:59:59Z", QueryOf(handler.UsageRequests[0])["ending_at"]);
    }

    [Fact]
    public async Task GetUsageAsync_AtMonthStartMidnight_StartingAtIsThatSameInstantUtc()
    {
        // E3: on 2026-12-01T00:00:00Z the month window starts at the same instant as "now".
        var clock = new FakeClock(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = RespondOk();
        var client = CreateClient(handler, clock);

        await client.GetUsageAsync(CancellationToken.None);

        foreach (var request in handler.UsageRequests.Concat(handler.CostRequests))
        {
            var query = QueryOf(request);
            Assert.Equal("2026-12-01T00:00:00Z", query["starting_at"]);
            Assert.Equal("2026-12-01T00:00:00Z", query["ending_at"]);
        }
    }

    [Fact]
    public async Task GetUsageAsync_SubSecondClock_SentTimestampsHaveNoFractionAndEndWithZ()
    {
        // E12e: starting_at/ending_at sent as "yyyy-MM-ddTHH:mm:ssZ" — fixed-width seconds, no
        // fractional digits, literal Z. Clock 2026-11-15T08:00:00.750Z → ending_at "…08:00:00Z".
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 8, 0, 0, 750, TimeSpan.Zero));
        var handler = RespondOk();
        var client = CreateClient(handler, clock);

        await client.GetUsageAsync(CancellationToken.None);

        foreach (var request in handler.UsageRequests.Concat(handler.CostRequests))
        {
            var query = QueryOf(request);
            Assert.Equal(MonthStartIso, query["starting_at"]);
            Assert.Equal(NowIso, query["ending_at"]);
        }
    }

    [Fact]
    public async Task GetUsageAsync_UnderThaiBuddhistCulture_DatesRemainGregorianUtcIso()
    {
        // The spec forbids any culture dependence; this machine's interactive culture is th-TH
        // (Buddhist calendar). Dates on the wire must stay Gregorian "yyyy-MM-ddTHH:mm:ssZ" (E3/C4).
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var handler = RespondOk();
            var client = CreateClient(handler);

            await client.GetUsageAsync(CancellationToken.None);

            var query = QueryOf(handler.UsageRequests[0]);
            Assert.Equal(MonthStartIso, query["starting_at"]);
            Assert.Equal(NowIso, query["ending_at"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---- C5/E9: fixed error messages ------------------------------------------------------------

    [Fact]
    public async Task GetUsageAsync_Usage401_MessageIsVerbatimC5Rejection()
    {
        // C5 with an echoing body: even if the server body repeats the key, the message is fixed.
        var handler = Respond(
            (_, _) => Status(
                HttpStatusCode.Unauthorized,
                "{\"error\":{\"key\":\"" + KeySentinel + "\",\"note\":\"" + BodyEchoMarker + "\"}}"),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(KeyRejectedMessage, ex.Message);
    }

    [Fact]
    public async Task GetUsageAsync_Cost403_MessageIsVerbatimC5Rejection()
    {
        // C5: 401/403 share the message; E5: one report failing → whole result fails.
        var handler = Respond(
            (_, _) => Success(UsageTodayOkSinglePage),
            (_, _) => Status(HttpStatusCode.Forbidden));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(KeyRejectedMessage, ex.Message);
    }

    [Fact]
    public async Task GetUsageAsync_Usage404_MessageIsVerbatimE9()
    {
        var handler = Respond(
            (_, _) => Status(HttpStatusCode.NotFound),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        // E9 overrides C5's 404 row verbatim.
        Assert.Equal(AdminApiNotAvailableMessage, ex.Message);
    }

    [Fact]
    public async Task GetUsageAsync_Cost404_MessageIsVerbatimE9()
    {
        var handler = Respond(
            (_, _) => Success(UsageTodayOkSinglePage),
            (_, _) => Status(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(AdminApiNotAvailableMessage, ex.Message);
    }

    [Fact]
    public async Task GetUsageAsync_429WithRetryAfter_MessageIsFixedC5()
    {
        var handler = Respond(
            (_, _) =>
            {
                var response = Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return response;
            },
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        // C5: the 429 message is the fixed short one (unlike the OAuth client's countdown — not required here).
        Assert.Equal(RateLimitedMessage, ex.Message);
    }

    [Fact]
    public async Task GetUsageAsync_Server500WithEchoingBody_SafeShortMessageNoKeyNoBody()
    {
        var handler = Respond(
            (_, _) => Status(
                HttpStatusCode.InternalServerError,
                "{\"error\":{\"type\":\"overloaded_error\",\"detail\":\"key " + KeySentinel + " " + BodyEchoMarker + "\"}}"),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        // C5/E8: short message for "other" statuses; never the key, headers, or response body.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BodyEchoMarker, ex.Message, StringComparison.Ordinal);
    }

    // ---- E12b: no inner exception on any failure path -------------------------------------------

    [Fact]
    public async Task GetUsageAsync_EveryFailurePath_ExceptionCarriesNoInnerException()
    {
        // E12b: AnthropicAdminApiException never carries an inner exception — status, transport,
        // parse and timeout failures all surface as a bare fixed-message exception.

        var statusHandler = Respond(
            (_, _) => Status(HttpStatusCode.Unauthorized), (_, _) => Success(CostTodayAndYesterday));
        var statusEx = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => CreateClient(statusHandler).GetUsageAsync(CancellationToken.None));
        Assert.Null(statusEx.InnerException);

        var transportHandler = Respond(
            (_, _) => throw new HttpRequestException("connect failed " + KeySentinel),
            (_, _) => throw new HttpRequestException("connect failed " + KeySentinel));
        var transportEx = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => CreateClient(transportHandler).GetUsageAsync(CancellationToken.None));
        Assert.Null(transportEx.InnerException);
        Assert.DoesNotContain(KeySentinel, transportEx.ToString(), StringComparison.Ordinal);

        var parseHandler = Respond(
            (_, _) => Success("not json at all"), (_, _) => Success(CostTodayAndYesterday));
        var parseEx = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => CreateClient(parseHandler).GetUsageAsync(CancellationToken.None));
        Assert.Null(parseEx.InnerException);

        var hangingHandler = new FakeHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            return Success(UsageTodayOkSinglePage);
        });
        var timeoutEx = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => CreateClient(hangingHandler, timeout: TimeSpan.FromMilliseconds(100))
                .GetUsageAsync(CancellationToken.None));
        Assert.Null(timeoutEx.InnerException);
    }

    // ---- E5: cursor/shape rules -------------------------------------------------------------

    [Fact]
    public async Task GetUsageAsync_HasMoreWithNullNextPage_FailsUnavailable()
    {
        // E5/E13b: has_more true with a null cursor → Unavailable BEFORE any further request — the
        // page-2 responder hands back a perfectly valid last page, so a client that wrongly
        // continues completes successfully instead of failing; the request count pins "exactly one".
        var handler = Respond(
            (_, index) => Success(index == 0 ? UsagePageWithCursorField("null") : UsagePageTwo),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Single(handler.UsageRequests);
    }

    [Fact]
    public async Task GetUsageAsync_HasMoreWithEmptyNextPage_FailsUnavailable()
    {
        // E13b (regression of E12c per Reviewer1 N2 / Reviewer2 #3): has_more true with the cursor
        // "" is as invalid as null — the property must be a NON-EMPTY string when has_more is true.
        // The old code's seen-cursor set alone accepted a first "" and then RE-FETCHED page 1 (the
        // URL omits an empty page parameter), summing it a second time when page 2 ends paging.
        // Page 2 is therefore a valid last page: accepting "" turns into success, not Unavailable.
        var handler = Respond(
            (_, index) => Success(index == 0 ? UsagePageWithCursorField("\"\"") : UsagePageTwo),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        // Unavailable after exactly one request — the client must not even try the continuation.
        Assert.Single(handler.UsageRequests);
    }

    [Fact]
    public async Task GetUsageAsync_CostHasMoreWithEmptyNextPage_FailsAfterOneRequest()
    {
        // E13b on the cost report: identical rule — has_more true with "" → Unavailable before any
        // further request. A valid last page at index 1 exposes acceptance as success.
        var handler = Respond(
            (_, _) => Success(UsageTodayOkSinglePage),
            (_, index) => Success(index == 0 ? CostPageWithCursorField("\"\"") : CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Single(handler.CostRequests);
    }

    [Fact]
    public async Task GetUsageAsync_CostHasMoreWithNullNextPage_FailsAfterOneRequest()
    {
        var handler = Respond(
            (_, _) => Success(UsageTodayOkSinglePage),
            (_, index) => Success(index == 0 ? CostPageWithCursorField("null") : CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Single(handler.CostRequests);
    }

    [Theory]
    // E13b: the next_page PROPERTY is required on every page — absent → Unavailable. (a) with
    // has_more true the client cannot continue anyway; (b) with has_more false the page is a valid
    // terminal page but the envelope is still incomplete per E5's required shapes
    // (data[], has_more, next_page). Reviewer2 #2 / Inspector B 3: both parsers accepted the
    // omission as null and SUCCEEDED — the false rows are the discriminating oracles.
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetUsageAsync_UsagePageMissingNextPageProperty_FailsUnavailable(bool hasMore)
    {
        var handler = Respond(
            (_, _) => Success(UsagePageWithCursorField(null, hasMore)),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.UsageRequests);
    }

    [Theory]
    // E13b on the cost report: same required-property rule (E5 lists next_page for both envelopes).
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetUsageAsync_CostPageMissingNextPageProperty_FailsUnavailable(bool hasMore)
    {
        var handler = Respond(
            (_, _) => Success(UsageTodayOkSinglePage),
            (_, _) => Success(CostPageWithCursorField(null, hasMore)));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.CostRequests);
    }

    [Fact]
    public async Task GetUsageAsync_RepeatedCursor_StopsAndFails()
    {
        // E5/E12c: a cursor seen before in the same report → Unavailable, and the client must stop
        // instead of looping.
        const string alwaysSame =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":true,"next_page":"cursor-1"}""";
        var handler = Respond(
            (_, _) => Success(alwaysSame),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(2, handler.UsageRequests.Count); // page 1 + page 2 (sees the repeat), then stop
    }

    [Fact]
    public async Task GetUsageAsync_Page10StillHasMore_FailsAfterTenPages()
    {
        // E5: at most 10 pages per report; page 10 still has_more → Unavailable.
        var handler = Respond(
            (_, index) => Success(UsagePageWithCursor(index, hasMore: true)),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(10, handler.UsageRequests.Count);
    }

    [Fact]
    public async Task GetUsageAsync_UsageFailsAndCostSucceeds_WholeResultFails()
    {
        // E5: "one report failing while the other succeeds → Unavailable" (both directions).
        var handler = Respond(
            (_, _) => Status(HttpStatusCode.InternalServerError),
            (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));
    }

    // ---- E4/E5/E12c: required shapes → Unavailable, never zero ----------------------------------

    [Theory]
    [InlineData(
        // output_tokens missing
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0}}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // cache_creation object missing entirely
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: cache_creation non-object (a string)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":"none","ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0,"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: cache_creation non-object (a number)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":5,"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: results non-array (an object)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":{}}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: results non-array (null)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":null}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: has_more non-Boolean (a string)
        """{"data":[],"has_more":"false","next_page":null}""")]
    [InlineData(
        // E12c: has_more non-Boolean (a number)
        """{"data":[],"has_more":0,"next_page":null}""")]
    [InlineData(
        // E12c: next_page non-string while has_more is true (a number; must be string-or-null)
        """{"data":[],"has_more":true,"next_page":123}""")]
    [InlineData(
        // E12c: next_page non-string even when has_more is false
        """{"data":[],"has_more":false,"next_page":true}""")]
    [InlineData(
        // ephemeral_1h_input_tokens missing inside cache_creation
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // uncached_input_tokens missing
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // cache_read_input_tokens missing
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // envelope "data" missing
        """{"has_more":false,"next_page":null}""")]
    [InlineData(
        // E5: envelope "data" non-array
        """{"data":{},"has_more":false,"next_page":null}""")]
    [InlineData(
        // envelope "has_more" missing (E5 required shapes: data[], has_more, next_page)
        """{"data":[]}""")]
    [InlineData(
        // token value as a string instead of a number
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":"1","cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // token value beyond long range (unparsable)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":99999999999999999999,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // body not JSON at all (E5: unparsable envelope → Unavailable)
        "not json at all")]
    public async Task GetUsageAsync_MalformedUsagePayload_FailsNeverZero(string malformedJson)
    {
        var handler = Respond((_, _) => Success(malformedJson), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        // E4/E5: missing token fields / bad envelope → whole result Unavailable, with a fixed safe
        // message (E7) — never raw parse diagnostics, never zero.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // E13c: JSON nodes that are not objects (root, bucket, result) → Unavailable WITHOUT an
    // InvalidOperationException escaping to the caller — JsonElement.TryGetProperty on a non-object
    // node throws InvalidOperationException internally, so the client must recognize the node kind
    // itself (or otherwise normalize the failure), never let the raw BCL diagnostic surface.
    // E12b additionally: no inner exception. The exact type asserted by ThrowsAsync IS the pin —
    // an escaped InvalidOperationException fails this test.
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("""{"data":[1],"has_more":false,"next_page":null}""")]
    [InlineData("""{"data":["bucket"],"has_more":false,"next_page":null}""")]
    [InlineData("""{"data":[null],"has_more":false,"next_page":null}""")]
    [InlineData("""{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[1]}],"has_more":false,"next_page":null}""")]
    [InlineData("""{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":["x"]}],"has_more":false,"next_page":null}""")]
    public async Task GetUsageAsync_UsageNonObjectNodes_FailAsApiExceptionNotInvalidOperation(string malformedJson)
    {
        var handler = Respond((_, _) => Success(malformedJson), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Null(ex.InnerException); // E12b
        Assert.DoesNotContain("InvalidOperationException", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal); // E8
    }

    [Theory]
    [InlineData(
        // negative amount
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"-5.00","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: amount with an explicit plus sign (AllowDecimalPoint only — no sign allowed)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"+5.00","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: amount with leading whitespace
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":" 123.45","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: amount with trailing whitespace
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45 ","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // non-USD currency (E4)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"EUR"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: currency non-string — "USD" must be a string
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":true}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // currency field missing (E5 required: amount, currency)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // amount missing
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // amount as a JSON number instead of the documented string (Facts: decimal string in cents)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":123.45,"currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E12c: amount with an exponent (plain decimal string only)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"1e5","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // amount with group separator — InvariantCulture decimal must reject it
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"1,234.50","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E13d: currency is compared ORDINAL against "USD" — the lowercase "usd" row that the
        // previous OrdinalIgnoreCase comparison accepted is Unavailable (Facts: currency is "USD").
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"usd"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // E13d: same rule on a mixed-case spelling — anything but the exact "USD" is Unavailable.
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"Usd"}]}],"has_more":false,"next_page":null}""")]
    public async Task GetUsageAsync_MalformedCostPayload_FailsNeverZero(string malformedJson)
    {
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(malformedJson));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // E13c on the cost report: non-object root/bucket/result nodes → AnthropicAdminApiException,
    // never the raw InvalidOperationException (E12b: no inner exception either).
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("""{"data":["bucket"],"has_more":false,"next_page":null}""")]
    [InlineData("""{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"USD"}]}],"has_more":false,"next_page":1}""")]
    [InlineData("""{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[1]}],"has_more":false,"next_page":null}""")]
    public async Task GetUsageAsync_CostNonObjectNodes_FailAsApiExceptionNotInvalidOperation(string malformedJson)
    {
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(malformedJson));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("InvalidOperationException", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    // E13d: a bucket timestamp must carry Z or an offset — DateTimeOffset.TryParse with
    // RoundtripKind silently reads a bare "2026-11-15T00:00:00" as LOCAL time (machine-dependent
    // day), so such a page is Unavailable instead — for starting_at and for ending_at, usage and
    // cost. The companion test below pins that a real OFFSET remains legal.
    [InlineData(true, "\"2026-11-15T00:00:00\"", "\"2026-11-16T00:00:00Z\"")]  // starting_at without offset
    [InlineData(true, "\"2026-11-15T00:00:00Z\"", "\"2026-11-16T00:00:00\"")]  // ending_at without offset
    [InlineData(false, "\"2026-11-15T00:00:00\"", "\"2026-11-16T00:00:00Z\"")] // cost report, same shapes
    [InlineData(false, "\"2026-11-15T00:00:00Z\"", "\"2026-11-16T00:00:00\"")] // cost ending_at too
    public async Task GetUsageAsync_BucketTimestampWithoutOffset_FailsUnavailable(
        bool usageLeg, string startingAtJson, string endingAtJson)
    {
        var page = usageLeg
            ? $$"""{"data":[{"starting_at":{{startingAtJson}},"ending_at":{{endingAtJson}},"results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}"""
            : $$"""{"data":[{"starting_at":{{startingAtJson}},"ending_at":{{endingAtJson}},"results":[{"amount":"123.45","currency":"USD"}]}],"has_more":false,"next_page":null}""";
        var handler = usageLeg
            ? Respond((_, _) => Success(page), (_, _) => Success(CostTodayAndYesterday))
            : Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(page));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetUsageAsync_BucketTimestampWithNonUtcOffset_IsAcceptedOnItsUtcDay()
    {
        // E13d/e companion (guards the new rule from over-rejecting): "must carry Z OR AN OFFSET" —
        // a +07:00 bucket starting 2026-11-15T00:00+07:00 is 2026-11-14T17:00Z, i.e. UTC day 14:
        // inside the month (E3), NOT today (clock 2026-11-15 08:00Z). Exact machine-independent
        // oracles: usage month 3 / today 0; cost month 1.2345m / today 0m.
        const string usagePage =
            """{"data":[{"starting_at":"2026-11-15T00:00:00+07:00","ending_at":"2026-11-16T00:00:00+07:00","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""";
        const string costPage =
            """{"data":[{"starting_at":"2026-11-15T00:00:00+07:00","ending_at":"2026-11-16T00:00:00+07:00","results":[{"amount":"123.45","currency":"USD"}]}],"has_more":false,"next_page":null}""";
        var handler = Respond((_, _) => Success(usagePage), (_, _) => Success(costPage));
        var client = CreateClient(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(3, result.MonthTokens);
        Assert.Equal(0, result.TodayTokens);
        Assert.Equal(1.2345m, result.MonthCostUsd);
        Assert.Equal(0m, result.TodayCostUsd);
    }

    // ---- E15c: one strict bucket-timestamp parser (usage and cost, starting_at and ending_at) ----

    private const string Day15Z = "2026-11-15T00:00:00Z";
    private const string Day16Z = "2026-11-16T00:00:00Z";

    private static string BucketPage(bool usageLeg, string startingAt, string endingAt) => usageLeg
        ? $$"""{"data":[{"starting_at":"{{startingAt}}","ending_at":"{{endingAt}}","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}"""
        : $$"""{"data":[{"starting_at":"{{startingAt}}","ending_at":"{{endingAt}}","results":[{"amount":"123.45","currency":"USD"}]}],"has_more":false,"next_page":null}""";

    private static RoutingHandler BucketHandler(bool usageLeg, string page) => usageLeg
        ? Respond((_, _) => Success(page), (_, _) => Success(CostTodayAndYesterday))
        : Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(page));

    public static TheoryData<bool, bool, string> StrictTimestampRejections()
    {
        // E15c: ISO 8601 yyyy-MM-ddTHH:mm:ss (+ optional fraction) ending in Z or ±hh:mm, parsed
        // exactly; anything else is Unavailable.
        var data = new TheoryData<bool, bool, string>();
        foreach (var bad in new[]
        {
            "Tue, 03-Nov-2026 00:00:00",   // free-form text a lenient TryParse accepts
            "Thu 2026-11-05 00:00:00",     // weekday prefix
            "2026-11-15t00:00:00Z",        // lowercase t
            "2026-11-15 00:00:00+00:00",   // space separator
            "2026-11-15T00:00:00",         // no zone
        })
        {
            foreach (var usageLeg in new[] { true, false })
            {
                foreach (var inStartingAt in new[] { true, false })
                {
                    data.Add(usageLeg, inStartingAt, bad);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StrictTimestampRejections))]
    public async Task GetUsageAsync_NonIsoBucketTimestamp_FailsUnavailable(bool usageLeg, bool inStartingAt, string badTimestamp)
    {
        var page = inStartingAt
            ? BucketPage(usageLeg, badTimestamp, Day16Z)
            : BucketPage(usageLeg, Day15Z, badTimestamp);
        var client = CreateClient(BucketHandler(usageLeg, page));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    // Clock 2026-11-15 08:00Z (today = UTC day 15; month = November). Usage oracle: 3 tokens per
    // bucket; cost oracle: 1.2345 USD per bucket.
    [InlineData("2026-11-15T00:00:00Z", true)]
    [InlineData("2026-11-14T23:59:59Z", false)]    // the second before: catches a local-zone shift either way
    [InlineData("2026-11-15T00:00:00.123Z", true)]
    [InlineData("2026-11-15T07:00:00+07:00", true)]    // = 2026-11-15T00:00Z
    [InlineData("2026-11-14T17:00:00-07:00", true)]    // = 2026-11-15T00:00Z, negative offset
    [InlineData("2026-11-15T06:59:59+07:00", false)]   // = 2026-11-14T23:59:59Z, the day before
    public async Task GetUsageAsync_IsoBucketTimestamp_IsAcceptedOnItsUtcDay(string startingAt, bool isToday)
    {
        foreach (var usageLeg in new[] { true, false })
        {
            var client = CreateClient(BucketHandler(usageLeg, BucketPage(usageLeg, startingAt, Day16Z)));

            var result = await client.GetUsageAsync(CancellationToken.None);

            if (usageLeg)
            {
                Assert.Equal(3, result.MonthTokens);
                Assert.Equal(isToday ? 3 : 0, result.TodayTokens);
            }
            else
            {
                Assert.Equal(1.2345m, result.MonthCostUsd);
                Assert.Equal(isToday ? 1.2345m : 0m, result.TodayCostUsd);
            }
        }
    }

    [Theory]
    [InlineData("2026-11-16T00:00:00Z")]
    [InlineData("2026-11-16T00:00:00.123Z")]
    [InlineData("2026-11-16T07:00:00+07:00")]
    public async Task GetUsageAsync_IsoBucketEndingAt_IsAccepted(string endingAt)
    {
        // E15c applies to ending_at too: the same three accepted shapes there.
        foreach (var usageLeg in new[] { true, false })
        {
            var client = CreateClient(BucketHandler(usageLeg, BucketPage(usageLeg, Day15Z, endingAt)));

            var result = await client.GetUsageAsync(CancellationToken.None);

            if (usageLeg)
            {
                Assert.Equal(3, result.MonthTokens);
            }
            else
            {
                Assert.Equal(1.2345m, result.MonthCostUsd);
            }
        }
    }

    // ---- E16: exact bucket-timestamp grammar (usage and cost, starting_at and ending_at) ----
    // Grammar: yyyy-MM-ddTHH:mm:ss, optional "." + 1-9 digits, then "Z" or ±hh:mm (hh 00-14,
    // mm 00-59, colon required); HH 00-23; uppercase T/Z; nothing before or after. Converted to
    // UTC, so every expectation below is a hand-computed UTC value (clock = 2026-11-15 08:00Z,
    // today = UTC day 15) that does not depend on the machine's zone (this machine is UTC+07:00).

    public static TheoryData<bool, bool, string> E16Rejections()
    {
        var data = new TheoryData<bool, bool, string>();
        foreach (var bad in new[]
        {
            "2026-11-15T00:00:00+0700",             // offset without colon
            "2026-11-15T00:00:00-0500",             // same, negative
            "2026-11-15T00:00:00+7:00",             // one-digit offset hour
            "2026-11-15T00:00:00-7:00",
            "2026-11-15T00:00:00+07",               // offset without minutes
            "2026-11-15T00:00:00+07:00:00",         // offset with seconds
            "2026-11-15T00:00:00.Z",                // dot without digits
            "2026-11-15T00:00:00.+07:00",           // dot without digits before an offset
            "2026-11-15T24:00:00Z",                 // hour 24
            "2026-11-15T24:00:00+07:00",            // hour 24 with an offset
            "2026-11-15T00:60:00Z",                 // minute 60
            "2026-11-15T00:00:00.1234567890Z",      // 10 fraction digits
            "2026-11-15T00:00:00.1234567890+07:00", // 10 fraction digits before an offset
            "2026-11-15t00:00:00Z",                 // lowercase t
            "2026-11-15T00:00:00z",                 // lowercase z
            "2026-11-15T00:00:00Z ",                // trailing space
            "2026-11-15T00:00:00Z\\n",              // trailing newline (JSON escape)
            " 2026-11-15T00:00:00Z",                // leading space
            "\\t2026-11-15T00:00:00Z",              // leading tab (JSON escape)
            "2026-11-15T00:00:00 Z",                // space before the zone
            "2026-11-15T00:00:00ZZ",                // trailing garbage
            "2026-11-15T00:00:00+15:00",            // offset hour 15
            "2026-11-15T00:00:00-15:00",
            "2026-11-15T00:00:00+07:60",            // offset minute 60
            "2026-11-15T00:00:00-05:60",
            "2026-11-15T00:00:00",                  // missing zone
            "2026-11-15T00:00:00.123",              // missing zone after a fraction
            "2026-13-15T00:00:00Z",                 // month 13 (not a calendar date)
            "2026-02-30T00:00:00Z",                 // day 30 of February
            "2026-11-15T00:00:60Z",                 // second 60
            "2026-11-15T0:00:00Z",                  // one-digit hour (fixed width)
            "2026-11-15T00:0:00Z",                  // one-digit minute
            "2026-11-15T00:00:0Z",                  // one-digit second
            "2026-11-5T00:00:00Z",                  // one-digit day
            "2026-1-15T00:00:00Z",                  // one-digit month
            "26-11-15T00:00:00Z",                   // two-digit year
            "2026-11-15T00:00:00+07:0",             // one-digit offset minute
            "\\uFF12026-11-15T00:00:00Z",           // fullwidth digit (JSON escape): ASCII digits only
        })
        {
            foreach (var usageLeg in new[] { true, false })
            {
                foreach (var inStartingAt in new[] { true, false })
                {
                    data.Add(usageLeg, inStartingAt, bad);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(E16Rejections))]
    public async Task GetUsageAsync_E16MalformedBucketTimestamp_FailsWithParseErrorMessage(
        bool usageLeg, bool inStartingAt, string badTimestamp)
    {
        var page = inStartingAt
            ? BucketPage(usageLeg, badTimestamp, Day16Z)
            : BucketPage(usageLeg, Day15Z, badTimestamp);
        var client = CreateClient(BucketHandler(usageLeg, page));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient.ParseErrorMessage, ex.Message);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal);
    }

    private static async Task AssertStartingAtOnUtcDayAsync(string startingAt, bool isToday)
    {
        // Usage oracle: 1+1+0+0+1 = 3 tokens per bucket. Cost oracle: "123.45" cents = 1.2345m.
        foreach (var usageLeg in new[] { true, false })
        {
            var client = CreateClient(BucketHandler(usageLeg, BucketPage(usageLeg, startingAt, Day16Z)));

            var result = await client.GetUsageAsync(CancellationToken.None);

            if (usageLeg)
            {
                Assert.Equal(3, result.MonthTokens);
                Assert.Equal(isToday ? 3 : 0, result.TodayTokens);
            }
            else
            {
                Assert.Equal(1.2345m, result.MonthCostUsd);
                Assert.Equal(isToday ? 1.2345m : 0m, result.TodayCostUsd);
            }
        }
    }

    [Theory]
    // 1, 7, 8 and 9 fraction digits, UTC. A fraction on 00:00:00 stays on day 15; a fraction on
    // 23:59:59 stays on day 14 (truncated or kept, never rounded up into day 15).
    [InlineData("2026-11-15T00:00:00.1Z", true)]
    [InlineData("2026-11-15T00:00:00.1234567Z", true)]
    [InlineData("2026-11-15T00:00:00.12345678Z", true)]
    [InlineData("2026-11-15T00:00:00.123456789Z", true)]
    [InlineData("2026-11-14T23:59:59.9Z", false)]
    [InlineData("2026-11-14T23:59:59.9999999Z", false)]
    [InlineData("2026-11-14T23:59:59.99999999Z", false)]
    [InlineData("2026-11-14T23:59:59.999999999Z", false)]
    // Fractions together with an offset.
    [InlineData("2026-11-15T07:00:00.123456789+07:00", true)]     // = 2026-11-15T00:00:00.123456789Z
    [InlineData("2026-11-15T06:59:59.999999999+07:00", false)]    // = 2026-11-14T23:59:59.999999999Z
    // +07:00 / -05:30 mapped to the right UTC day.
    [InlineData("2026-11-15T07:00:00+07:00", true)]               // = 11-15T00:00Z
    [InlineData("2026-11-15T06:59:59+07:00", false)]              // = 11-14T23:59:59Z
    [InlineData("2026-11-14T18:30:00-05:30", true)]               // = 11-15T00:00Z (18:30 + 5:30)
    [InlineData("2026-11-14T18:29:59-05:30", false)]              // = 11-14T23:59:59Z
    [InlineData("2026-11-15T05:45:00+05:45", true)]               // nonzero offset minutes: = 11-15T00:00Z
    [InlineData("2026-11-15T05:44:59+05:45", false)]              // = 11-14T23:59:59Z
    // Offset hour 14 is the legal maximum, both signs.
    [InlineData("2026-11-15T14:00:00+14:00", true)]               // = 11-15T00:00Z
    [InlineData("2026-11-14T10:00:00-14:00", true)]               // = 11-15T00:00Z (10:00 + 14:00)
    [InlineData("2026-11-15T13:59:59+14:00", false)]              // = 11-14T23:59:59Z
    [InlineData("2026-11-15T14:30:00+14:30", true)]               // hh 14 with mm 30 is inside the grammar: = 11-15T00:00Z
    [InlineData("2026-11-14T09:30:00-14:30", true)]               // = 11-15T00:00Z (09:30 + 14:30)
    // Hour 23 is legal; Z is UTC, not the machine's zone (UTC+07:00 here).
    [InlineData("2026-11-14T23:59:59Z", false)]
    public async Task GetUsageAsync_E16ValidBucketStartingAt_IsMappedToItsUtcDay(string startingAt, bool isToday) =>
        await AssertStartingAtOnUtcDayAsync(startingAt, isToday);

    [Theory]
    [InlineData("2026-11-16T00:00:00.1Z")]
    [InlineData("2026-11-16T00:00:00.1234567Z")]
    [InlineData("2026-11-16T00:00:00.12345678Z")]
    [InlineData("2026-11-16T00:00:00.123456789Z")]
    [InlineData("2026-11-15T23:59:59Z")]                  // hour 23
    [InlineData("2026-11-16T07:00:00.123456789+07:00")]   // fraction + offset
    [InlineData("2026-11-15T18:30:00-05:30")]             // negative offset with minutes
    [InlineData("2026-11-16T14:00:00+14:00")]             // maximum offset hour
    public async Task GetUsageAsync_E16ValidBucketEndingAt_IsAccepted(string endingAt)
    {
        foreach (var usageLeg in new[] { true, false })
        {
            var client = CreateClient(BucketHandler(usageLeg, BucketPage(usageLeg, Day15Z, endingAt)));

            var result = await client.GetUsageAsync(CancellationToken.None);

            if (usageLeg)
            {
                Assert.Equal(3, result.MonthTokens);
                Assert.Equal(3, result.TodayTokens);
            }
            else
            {
                Assert.Equal(1.2345m, result.MonthCostUsd);
                Assert.Equal(1.2345m, result.TodayCostUsd);
            }
        }
    }

    [Fact]
    public async Task GetUsageAsync_TokenSumOverflowsLong_FailsNotWraps()
    {
        // E4: token sums use checked long. Day15: output = long.MaxValue (everything else 0);
        // day16(today): uncached = 1 → the month sum would overflow → whole result Unavailable,
        // never a wrapped negative.
        const string page =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":0,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":9223372036854775807}]},{"starting_at":"2026-11-16T00:00:00Z","ending_at":"2026-11-17T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":0}]}],"has_more":false,"next_page":null}""";
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero));
        var handler = Respond((_, _) => Success(page), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler, clock);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetUsageAsync_TokenSumWithinOneBucketOverflows_FailsNotWraps()
    {
        // E12c: "all token additions checked" — a single bucket whose own fields sum past
        // long.MaxValue is an overflow too: the whole Admin result is Unavailable (E4, never zero,
        // never a wrapped negative value on the result).
        const string page =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":9223372036854775807,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""";
        var handler = Respond((_, _) => Success(page), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetUsageAsync_EmptyMonthBuckets_SucceedsWithZeroes()
    {
        // Legitimate zero (NOT the E4 failure zero): the API returned successfully with no usage in
        // the window. "Buckets oldest first, empty buckets have empty results" (Facts).
        const string emptyUsage = """{"data":[],"has_more":false,"next_page":null}""";
        const string emptyCost = """{"data":[],"has_more":false,"next_page":null}""";
        var handler = Respond((_, _) => Success(emptyUsage), (_, _) => Success(emptyCost));
        var client = CreateClient(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(0, result.MonthTokens);
        Assert.Equal(0, result.TodayTokens);
        Assert.Equal(0m, result.MonthCostUsd);
        Assert.Equal(0m, result.TodayCostUsd);
    }

    // ---- E7: deadline, body cap, cancellation ----------------------------------------------------

    [Fact]
    public async Task GetUsageAsync_PerRequestTimeout_SafeFixedMessageNotCancellation()
    {
        // E7: ONE overall 20 s deadline in production; inject a short timeout so the test stays
        // fast (house pattern from the OAuth client). A slow response must become a fixed safe
        // failure message, not an OperationCanceledException escaping to the caller (E7
        // distinguishes caller-cancellation).
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            return Success(UsageTodayOkSinglePage);
        });
        var client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient.TimeoutMessage, ex.Message);
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUsageAsync_OverLimitBody_FailsWithSafeMessage()
    {
        // E7: response body ≤ 1 MB per page (bounded read like the OAuth client). Default 1 000 000;
        // inject a small cap. The oversized page carries the sentinel to prove nothing is echoed.
        var padding = new string('x', 4096) + KeySentinel;
        var body = "{\"data\":[{\"starting_at\":\"2026-11-15T00:00:00Z\",\"ending_at\":\"2026-11-16T00:00:00Z\","
            + "\"results\":[{\"model\":\"m\",\"uncached_input_tokens\":1,\"cache_read_input_tokens\":1,"
            + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":0},\"output_tokens\":1}]}],"
            + "\"has_more\":false,\"next_page\":null,\"padding\":\"" + padding + "\"}";
        var handler = Respond((_, _) => Success(body), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler, maxResponseBodyBytes: 256);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDefaults_AreTwentySecondsAndOneMillionBytes()
    {
        // E13g/E7: the production defaults are public so tests pin them: ONE overall deadline of
        // 20 s (C4's older 15 s is superseded) and a response body cap of 1,000,000 bytes per page.
        Assert.Equal(TimeSpan.FromSeconds(20), AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient.DefaultTimeout);
        Assert.Equal(1_000_000, AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient.DefaultMaxResponseBodyBytes);
    }

    [Fact]
    public void Timeout_WithoutExplicitTimeout_IsTwentySeconds()
    {
        // E15b: the effective deadline is exposed as a public read-only Timeout; with no timeout
        // argument it is the E7 production default of 20 s (literal, not DefaultTimeout).
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(RespondOk()), KeySentinel, new FakeClock(NowMidMonthUtc));

        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1500)]
    [InlineData(45_000)]
    public void Timeout_WithExplicitTimeout_IsThatValue(int milliseconds)
    {
        // E15b: "(constructor timeout ?? DefaultTimeout)" — an explicit value wins.
        var client = CreateClient(RespondOk(), timeout: TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), client.Timeout);
    }

    /// <summary>A valid single-page, no-more usage body padded with an ignored string property so
    /// that its UTF-8 size is exactly <paramref name="totalBytes"/> (pure ASCII: 1 char = 1 byte).</summary>
    private static string UsageBodyOfExactSize(int totalBytes)
    {
        const string head =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null,"padding":""" + "\"";
        const string tail = "\"}";
        var padLength = totalBytes - head.Length - tail.Length;
        Assert.True(padLength > 0);
        var body = head + new string('x', padLength) + tail;
        Assert.Equal(totalBytes, System.Text.Encoding.UTF8.GetByteCount(body));
        return body;
    }

    [Fact]
    public async Task GetUsageAsync_DefaultCap_BodyOfExactlyOneMillionBytes_IsAccepted()
    {
        // E7/E13g: "response body ≤ 1 MB per page" with the production cap of 1,000,000 bytes (NO
        // maxResponseBodyBytes override): a page of exactly 1,000,000 bytes is still within the cap.
        var handler = Respond((_, _) => Success(UsageBodyOfExactSize(1_000_000)), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(3, result.MonthTokens); // 1+1+0+0+1 — the page really was parsed
    }

    [Fact]
    public async Task GetUsageAsync_DefaultCap_BodyOfOneMillionAndOneBytes_FailsUnavailable()
    {
        // E7/E13g: one byte over the production cap → Unavailable (fixed safe message, no echo).
        var handler = Respond((_, _) => Success(UsageBodyOfExactSize(1_000_001)), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task GetUsageAsync_AlreadyCancelledToken_ThrowsWithoutAnyRequest()
    {
        var handler = RespondOk();
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // E7: caller cancellation propagates as OperationCanceledException and is never turned into
        // "Unavailable". Checked before any network access (house rule from the OAuth client).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetUsageAsync(cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetUsageAsync_CallerCancelsMidRequest_PropagatesOperationCanceled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            token.Register(() => release.TrySetCanceled(token));
            entered.TrySetResult();
            return await release.Task.ConfigureAwait(false);
        });
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();

        var pending = client.GetUsageAsync(cts.Token);
        await entered.Task;
        cts.Cancel();

        // E7: never mapped to Unavailable — the caller sees OperationCanceledException (or a derived
        // TaskCanceledException) and nothing else.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task GetUsageAsync_CallerCancelsWhileBodyReadBlocks_PropagatesOperationCanceled()
    {
        // E7/E13 (Inspector B 6): the CANCELLING BODY-READ path, not just the send — the response
        // stream's ReadAsync blocks until the token fires. A client that only wires cancellation at
        // SendAsync would sit here until the deadline and return "Unavailable" instead. The cost
        // report answers normally so the hanging usage read is the one and only open request when
        // the caller cancels.
        var hanging = new HangingReadStream();
        var handler = new RoutingHandler(
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(hanging) }),
            (_, _, _) => Task.FromResult(Success(CostTodayAndYesterday)));
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();

        var pending = client.GetUsageAsync(cts.Token);
        await hanging.ReadEntered; // the client is exactly inside the body read now
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task GetUsageAsync_BodyReadBlocksPastDeadline_FailsWithSafeMessageNotCancellation()
    {
        // E7 the other way: only the CALLER's cancellation may surface as OperationCanceledException.
        // Both report bodies hang in ReadAsync until the overall deadline fires; the unwinding of a
        // timed-out read must become the fixed safe failure, never a cancellation escaping to the
        // caller and never raw diagnostics (E8: no sentinel anywhere, E12b: no inner exception).
        var usageHanging = new HangingReadStream();
        var costHanging = new HangingReadStream();
        var handler = new RoutingHandler(
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(usageHanging) }),
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(costHanging) }));
        var client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient.TimeoutMessage, ex.Message);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    // ---- E12d: cancelled caller token after an Admin failure; sibling cancellation ---------------

    [Fact]
    public async Task GetUsageAsync_CallerCancelsAfterAdminFailure_ThrowsOperationCanceledNotUnavailable()
    {
        // E12d/E13c: after any Admin failure, if the caller token is cancelled the call throws
        // OperationCanceledException — the caller is never handed "Unavailable" that it did not ask
        // for. Deterministic ordering (no wall-clock, no race between the test thread and the
        // client): the usage handler cancels the CALLER token itself and then answers 401, so by the
        // time the Admin failure reaches the unwind the caller token is cancelled — the exact state
        // E12d describes. (The previous shape cancelled from the test thread after the call; the
        // client fails the 401 synchronously, so the call had already completed with "Unavailable"
        // before the cancel ever happened — a test race, not a code defect.)
        using var cts = new CancellationTokenSource();
        var handler = new RoutingHandler(
            (_, _, _) =>
            {
                cts.Cancel();
                return Task.FromResult(Status(HttpStatusCode.Unauthorized));
            },
            (_, _, _) => Task.FromResult(Success(CostTodayAndYesterday)));
        var client = CreateClient(handler);

        // Every surfacing path must end as OperationCanceledException — never the fixed
        // AnthropicAdminApiException message that the tab would render as "Unavailable".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetUsageAsync(cts.Token));
    }

    [Fact]
    public async Task GetUsageAsync_UsageFailsWhileCostSendBlocked_LongDeadline_StopsSiblingBeforeDeadline()
    {
        // E13a (Reviewer1 N1 / Reviewer2 #1) + E12d: a failing report must cancel the shared linked
        // token source from INSIDE the failing task, so the sibling stops at once — not after
        // Task.WhenAll, which only unwinds once the sibling already completed. Prescribed
        // discriminating shape: a LONG client timeout (30 s), the cost request blocks on a
        // TaskCompletionSource tied to its own token, usage then fails 401. Deterministic ordering:
        // the usage handler waits until the cost request is really blocked inside its send before it
        // answers 401 (otherwise the client would cancel the sibling before it even started and the
        // sibling "never saw" anything). Both legs: (i) the cost request observed cancellation, and
        // (ii) GetUsageAsync returned WITHOUT waiting for the deadline — a 10 s guard stands in for
        // "long before 30 s". Against the pre-E13a code the only thing that ever releases the blocked
        // cost send is the 30 s deadline, so the guard fires: that is the red this test pins.
        var costEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var costSawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var costBlocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RoutingHandler(
            async (_, _, _) =>
            {
                await costEntered.Task.ConfigureAwait(false); // cost is blocked in its send now
                return Status(HttpStatusCode.Unauthorized);
            },
            async (_, _, token) =>
            {
                using var registration = token.Register(() => costBlocked.TrySetCanceled(token));
                costEntered.TrySetResult();
                try
                {
                    return await costBlocked.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    costSawCancellation.TrySetResult();
                    throw;
                }
            });
        var client = CreateClient(handler, timeout: TimeSpan.FromSeconds(30));

        var pending = client.GetUsageAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(KeyRejectedMessage, ex.Message);
        Assert.True(
            costSawCancellation.Task.IsCompleted,
            "the sibling cost request never observed cancellation (E13a)");
    }

    [Fact]
    public async Task GetUsageAsync_UsageFailsWhileCostBodyReadBlocked_LongDeadline_StopsSiblingRead()
    {
        // E13a/E12d "stops its next send/read": the same sibling-cancel rule on the BODY-READ leg.
        // The cost report's response body blocks in ReadAsync until its token fires; the usage page
        // fails 401 only once that read is really in flight. A 30 s deadline cannot be what ends the
        // read; the failing report's immediate cancel must (10 s guard), and the blocked read must
        // have seen the cancellation. The Admin failure — not a cancellation — reaches the caller.
        var costBody = new HangingReadStream();
        var handler = new RoutingHandler(
            async (_, _, _) =>
            {
                await costBody.ReadEntered.ConfigureAwait(false);
                return Status(HttpStatusCode.Unauthorized);
            },
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(costBody) }));
        var client = CreateClient(handler, timeout: TimeSpan.FromSeconds(30));

        var pending = client.GetUsageAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(KeyRejectedMessage, ex.Message);
        Assert.True(costBody.ReadCancelled.IsCompleted, "the sibling's blocked body read was never cancelled (E13a)");
    }

    [Fact]
    public async Task GetUsageAsync_UsageFailsWhileCostPage2Blocked_LongDeadline_SiblingStopsMidPagination()
    {
        // E12d/E13a on a PAGED sibling: cost page 1 answers with a cursor, page 2 blocks on a source
        // tied to its token; usage fails 401 only once page 2 is in flight. The sibling must be
        // cancelled in the middle of its pagination, long before the 30 s deadline, and never be
        // asked for a page 3.
        var costPage2Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var costPage2Cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var page2Blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RoutingHandler(
            async (_, _, _) =>
            {
                await costPage2Entered.Task.ConfigureAwait(false);
                return Status(HttpStatusCode.Unauthorized);
            },
            async (_, index, token) =>
            {
                if (index == 0)
                {
                    return Success("""{"data":[],"has_more":true,"next_page":"cursor-1"}""");
                }

                using var registration = token.Register(() => page2Blocked.TrySetCanceled(token));
                costPage2Entered.TrySetResult();
                try
                {
                    return await page2Blocked.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    costPage2Cancelled.TrySetResult();
                    throw;
                }
            });
        var client = CreateClient(handler, timeout: TimeSpan.FromSeconds(30));

        var pending = client.GetUsageAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(KeyRejectedMessage, ex.Message);
        Assert.True(costPage2Cancelled.Task.IsCompleted, "the sibling's page-2 request was not cancelled (E13a)");
        Assert.Equal(2, handler.CostRequests.Count); // page 1 + the cancelled page 2, no page 3
    }

    [Theory]
    // E13c (Reviewer2 #4): while the caller token is cancelled, ANY exception leaving an Admin
    // request — including a non-HTTP one carrying raw diagnostics — must surface as
    // OperationCanceledException and never as the raw exception with its sentinel-bearing text
    // (E7/E8). The old catch filter `when (!caller.IsCancellationRequested)` skipped the
    // normalization exactly when the caller had cancelled, letting these escape verbatim.
    // The usage page 2 (after a valid cursor page) cancels the caller and throws: at that point
    // the cost report is already complete, so the raw exception is the one and only failure
    // reaching the unwind — deterministic.
    [InlineData("invalidOperation")]
    [InlineData("io")]
    [InlineData("format")]
    public async Task GetUsageAsync_CallerCancelledWithNonHttpException_SurfacesCancellationWithoutRawText(string kind)
    {
        using var cts = new CancellationTokenSource();
        Exception raw = kind switch
        {
            "invalidOperation" => new InvalidOperationException($"cannot get value for property near '{KeySentinel}'"),
            "io" => new IOException($"stream corrupted at marker {KeySentinel}"),
            _ => new FormatException($"input not well formed: {KeySentinel}"),
        };
        var costResponded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RoutingHandler(
            async (_, index, _) =>
            {
                if (index == 0)
                {
                    // Page only after the cost request is out of the way, so the cancellation
                    // below races NOTHING else: the raw exception is the failure that reaches the
                    // unwind.
                    await costResponded.Task.ConfigureAwait(false);
                    return Success(UsagePageWithCursorField("\"cursor-1\""));
                }

                cts.Cancel(); // the caller has gone away ...
                throw raw;    // ... and this report dies to a non-HTTP exception carrying raw text
            },
            (_, _, _) =>
            {
                var response = Success(CostTodayAndYesterday);
                costResponded.TrySetResult();
                return Task.FromResult(response);
            });
        var client = CreateClient(handler);

        // E13c: not the raw exception, not any AnthropicAdminApiException — cancellation, full stop.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetUsageAsync(cts.Token));

        // E8: nothing of the raw diagnostic reaches the caller.
        Assert.DoesNotContain(raw.Message, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUsageAsync_TransportFailure_SafeMessageWithoutKeyOrUrlEcho()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("connect failed to https://api.anthropic.com " + KeySentinel)));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        // C5/E8: network failures get a short message; the inner exception's text (which here contains
        // the sentinel) must not be forwarded. E12b: no inner exception at all.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    /// <summary>A response-body stream whose async reads block until the read token is cancelled —
    /// the E13 body-read leg (Inspector B 6): cancellation during the send was covered, the read
    /// path was not. <see cref="ReadEntered"/> completes when a read starts, so the test cancels
    /// exactly while the client is inside <c>ReadAsync</c>. Bytes never arrive; a released gate
    /// simply ends the stream (EOF).</summary>
    private sealed class HangingReadStream : Stream
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadEntered => _entered.Task;

        /// <summary>Completes when a read was ended by its token (the cancellation reached the read).</summary>
        public Task ReadCancelled => _cancelled.Task;

        /// <summary>Only for tests that need the read to finish instead of being cancelled.</summary>
        public void Release() => _blocked.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            using (cancellationToken.Register(() =>
            {
                _cancelled.TrySetResult();
                _blocked.TrySetCanceled(cancellationToken);
            }))
            {
                await _blocked.Task.ConfigureAwait(false);
            }

            return 0; // EOF — only reached when a test releases the gate.
        }
    }
}
