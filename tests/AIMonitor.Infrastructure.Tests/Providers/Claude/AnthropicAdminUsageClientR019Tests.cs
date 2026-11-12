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
        const string broken =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":true,"next_page":null}""";
        var handler = Respond((_, _) => Success(broken), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        // E5: has_more true with a null cursor → the whole Admin result is Unavailable.
        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetUsageAsync_HasMoreWithEmptyNextPage_FailsUnavailable()
    {
        const string broken =
            """{"data":[],"has_more":true,"next_page":""}""";
        var handler = Respond((_, _) => Success(broken), (_, _) => Success(CostTodayAndYesterday));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));
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
    public async Task GetUsageAsync_MalformedCostPayload_FailsNeverZero(string malformedJson)
    {
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(malformedJson));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
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

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
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

    // ---- E12d: cancelled caller token after an Admin failure; sibling cancellation ---------------

    [Fact]
    public async Task GetUsageAsync_CallerCancelsAfterAdminFailure_ThrowsOperationCanceledNotUnavailable()
    {
        // E12d: after any Admin failure, if the caller token is cancelled the call throws
        // OperationCanceledException — the caller is never handed "Unavailable" that it did not ask
        // for. The usage report fails 401 immediately; the cost page lingers (its fake deliberately
        // ignores cancellation so the Admin failure — not a cancellation — is what ends the call),
        // giving the test a wide, deterministic window to cancel the caller in between.
        var usageEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RoutingHandler(
            (_, _, _) =>
            {
                usageEntered.TrySetResult();
                return Task.FromResult(Status(HttpStatusCode.Unauthorized));
            },
            async (_, _, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), CancellationToken.None).ConfigureAwait(false);
                return Success(CostTodayAndYesterday);
            });
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();

        var pending = client.GetUsageAsync(cts.Token);
        await usageEntered.Task;
        cts.Cancel(); // lands well inside the 150 ms window, before the failure handling completes

        // Every surfacing path must end as OperationCanceledException — never the fixed
        // AnthropicAdminApiException message that the tab would render as "Unavailable".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task GetUsageAsync_OneReportFailsWhileOtherPending_SiblingIsCancelledAndStops()
    {
        // E12d: one report failing cancels its sibling — the still-in-flight sibling request must
        // observe cancellation instead of running on, and the Admin failure (not a cancellation)
        // reaches the caller. The usage page fails 401 at once; the cost report paginates and its
        // page 2 hangs (observing its token) until the client unwinds the refresh.
        var costPage2SawCancellation = false;
        var handler = new RoutingHandler(
            (_, _, _) => Task.FromResult(Status(HttpStatusCode.Unauthorized)),
            async (_, index, token) =>
            {
                if (index == 0)
                {
                    return Success("""{"data":[],"has_more":true,"next_page":"cursor-1"}""");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    costPage2SawCancellation = true;
                    throw;
                }

                return Success(CostTodayAndYesterday);
            });
        var client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(400));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminApiException>(
            () => client.GetUsageAsync(CancellationToken.None));

        Assert.Equal(KeyRejectedMessage, ex.Message);
        Assert.True(costPage2SawCancellation, "the sibling cost request was not cancelled");
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
}
