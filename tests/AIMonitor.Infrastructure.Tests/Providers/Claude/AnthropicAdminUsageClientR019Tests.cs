using System.Globalization;
using System.Net;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// R-019 (written from Docs/SPEC_R019.md alone): C4/C5 + decisions E3/E4/E5/E7/E8 for the new
/// <c>AnthropicAdminUsageClient</c>. Fake <see cref="HttpMessageHandler"/> only — no real network, key,
/// clock or culture dependence.
///
/// <para><b>Expected API (this is the alignment contract for the Coding Owner after the R-019 merge).</b>
/// The spec names the type only; everything else below is the test author's best-shape guess modelled
/// on the sanctioned pattern from the spec ("OpenAI's Admin key path ... is the pattern to follow"):</para>
/// <code>
/// namespace AIMonitor.Infrastructure.Providers.Claude;
///
/// sealed class AnthropicAdminUsageClient(
///     HttpClient httpClient,                       // caller-owned, never disposed (house rule)
///     IClock clock,                                // E3: UTC month/today boundaries from clock.UtcNow
///     TimeSpan? timeout = null,                    // per-request, default 15 s (C4)
///     int? maxResponseBodyBytes = null)            // per page, default 1 000 000 (E7)
/// {
///     Task&lt;AnthropicAdminUsageResult&gt; GetUsageAndCostAsync(string apiKey, CancellationToken ct);
/// }
///
/// sealed record AnthropicAdminUsageResult(       // E4: decimal money, checked long token sums
///     long MonthTokens, long TodayTokens, decimal MonthCostUsd, decimal TodayCostUsd);
///
/// sealed class AnthropicAdminUsageException : Exception   // every C5/E4/E5/E7 failure, fixed safe Message;
///                                                          // caller cancellation is NEVER converted (E7)
/// </code>
///
/// <para><b>Why these tests fail on base 29f4048:</b> the implementation lives on the Writing Owner's
/// branch. Until it merges, none of the above exists, so every test below <see cref="Assert"/>-fails via
/// the <c>R019_IMPLEMENTED</c> guard (keep the solution and the whole rest of the suite buildable).
/// To activate the real bodies: build with <c>-p:DefineConstants=R019_IMPLEMENTED</c> (or add the
/// constant to this test csproj after the merge) and rename members where the implementation differs.
/// C5/E9 error texts and the E10 money/token display rules are pinned verbatim from the spec.</para>
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

    // E8 sentinel: must never appear in any exception text (or anywhere else).
    private const string KeySentinel = "sk-an…-6c2e";

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

    // A bucket dated before the current UTC month — must still be counted into "month" only via the
    // request window; these fixtures keep all buckets inside the month per E3.

    // ---- Shared helpers (exist on base; only used inside the guarded bodies) ----------------

    private static HttpResponseMessage Success(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body) };

    /// <summary>Routes requests by path: the usage report responder is called with a per-kind index so
    /// pagination fixtures can hand out page 1, page 2, ... in order.</summary>
    private static FakeHttpMessageHandler Respond(
        Func<HttpRequestMessage, int, HttpResponseMessage> usageResponder,
        Func<HttpRequestMessage, int, HttpResponseMessage> costResponder)
    {
        var usageCalls = 0;
        var costCalls = 0;
        return new FakeHttpMessageHandler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.Contains("cost_report", StringComparison.Ordinal))
            {
                return Task.FromResult(costResponder(request, costCalls++));
            }

            return Task.FromResult(usageResponder(request, usageCalls++));
        });
    }

    private static FakeHttpMessageHandler RespondOk() =>
        Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(CostTodayAndYesterday));

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

    private static string GuardrailNotMergedMessage() =>
        "R-019 not on this branch (base 29f4048): AnthropicAdminUsageClient / AnthropicAdminUsageResult / "
        + "AnthropicAdminUsageException do not exist yet. Activate by building with "
        + "-p:DefineConstants=R019_IMPLEMENTED after the Writing Owner's implementation merges, then align "
        + "member names per the file header.";

    // ---- C4: request shape --------------------------------------------------------------------

    [Fact]
    public async Task GetUsageAndCostAsync_UsageRequest_UrlQueryAndHeadersMatchSpec()
    {
#if R019_IMPLEMENTED
        var handler = RespondOk();
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
        Assert.Equal("/v1/organizations/usage_report/messages", request.RequestUri.AbsolutePath);

        // C4 + E3: starting_at = first day of current UTC month at 00:00Z, ending_at = clock now.
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
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_CostRequest_UrlQueryHeadersMatchNoGroupBy()
    {
#if R019_IMPLEMENTED
        // The fake's LastRequest would be the second of the two reports; use a recording fake that
        // keeps every request so the cost request can be named exactly.
        var recording = new RecordingHandler();
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(recording), new FakeClock(NowMidMonthUtc));

        await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        var cost = Assert.Single(recording.CostRequests);
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
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    // ---- C4/E3/E5: two-page sums with distinct fields ----------------------------------------

    [Fact]
    public async Task GetUsageAndCostAsync_TwoPageUsage_SumsAllFiveTokenFieldsHandComputed()
    {
#if R019_IMPLEMENTED
        var usagePagesSeen = new List<string?>();
        var handler = Respond(
            (request, index) =>
            {
                var q = QueryOf(request);
                usagePagesSeen.Add(q.TryGetValue("page", out var p) ? p : null);
                return Success(index == 0 ? UsagePageOne : UsagePageTwo);
            },
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var result = await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        // Oracles (see fixture comments):
        //   day13 = (1000+2000+3000+4000+5000) + (100+200+300+400+500) = 15000 + 1500 = 16500
        //   day14 = 0 (empty bucket), today = 7+11+13+17+19 = 67
        //   month = 16500 + 0 + 67 = 16567
        Assert.Equal(16567, result.MonthTokens);
        Assert.Equal(67, result.TodayTokens);

        // E5/C4: page 2 is requested with the previous next_page as `page`.
        Assert.Equal(2, usagePagesSeen.Count);
        Assert.Null(usagePagesSeen[0]);
        Assert.Equal("cursor-1", usagePagesSeen[1]);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_CostCentsString_ParsesInvariantExactDecimal()
    {
#if R019_IMPLEMENTED
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(CostWithOneCentBucket));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var result = await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        // E4: decimal string in cents ÷ 100 (InvariantCulture), summed exactly — no float drift:
        //   today  = "123.45" cents                                  → 1.2345m
        //   month  = "0.01" (0.0001m) + "100.00" (1.0000m) + "123.45" (1.2345m) → 2.2346m
        Assert.Equal(1.2345m, result.TodayCostUsd);
        Assert.Equal(2.2346m, result.MonthCostUsd);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    // ---- E3: UTC day/month boundaries -----------------------------------------------------------

    [Fact]
    public async Task GetUsageAndCostAsync_AtExactMidnightUtc_TodayIsTheNewDayBucket()
    {
#if R019_IMPLEMENTED
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
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), clock);

        var result = await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        Assert.Equal(1500, result.TodayTokens);                 // only the 16th bucket: 100+200+300+400+500
        Assert.Equal(16500, result.MonthTokens);                 // 15000 + 1500
        Assert.Equal(MonthStartIso, QueryOf(handler.LastRequest!)["starting_at"]);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_At2359Utc_TodayIsStillThatUtcDayBucket()
    {
#if R019_IMPLEMENTED
        // Clock at 2026-11-15T23:59:59Z — the last record of the UTC day: the bucket starting
        // 2026-11-15T00:00:00Z is still today (E3: UTC, never local).
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 23, 59, 59, TimeSpan.Zero));
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), clock);

        var result = await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        Assert.Equal(67, result.TodayTokens);
        Assert.Equal(67, result.MonthTokens);
        Assert.Equal("2026-11-15T23:59:59Z", QueryOf(handler.LastRequest!)["ending_at"]);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_AtMonthStartMidnight_StartingAtIsThatSameInstantUtc()
    {
#if R019_IMPLEMENTED
        // E3: on 2026-12-01T00:00:00Z the month window starts at the same instant as "now".
        var clock = new FakeClock(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), clock);

        await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        var usageQuery = QueryOf(handler.LastRequest!);
        Assert.Equal("2026-12-01T00:00:00Z", usageQuery["starting_at"]);
        Assert.Equal("2026-12-01T00:00:00Z", usageQuery["ending_at"]);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_UnderThaiBuddhistCulture_DatesRemainGregorianUtcIso()
    {
#if R019_IMPLEMENTED
        // The spec forbids any culture dependence; this machine's interactive culture is th-TH
        // (Buddhist calendar). Dates on the wire must stay Gregorian "yyyy-MM-ddTHH:mm:ssZ" (E3/C4).
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var handler = RespondOk();
            var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
                new HttpClient(handler), new FakeClock(NowMidMonthUtc));

            await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

            var query = QueryOf(handler.LastRequest!);
            Assert.Equal(MonthStartIso, query["starting_at"]);
            Assert.Equal(NowIso, query["ending_at"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    // ---- C5/E9: fixed error messages ------------------------------------------------------------

    [Fact]
    public async Task GetUsageAndCostAsync_Usage401_MessageIsVerbatimC5Rejection()
    {
#if R019_IMPLEMENTED
        // C5 with an echoing body: even if the server body repeats the key, the message is fixed.
        var handler = Respond(
            (_, _) => Status(HttpStatusCode.Unauthorized, $$"""{"error":{"key":"{{KeySentinel}}","note":"{{BodyEchoMarker}}"}}"""),
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.Equal(KeyRejectedMessage, ex.Message);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_Cost403_MessageIsVerbatimC5Rejection()
    {
#if R019_IMPLEMENTED
        // C5: 401/403 share the message; E5: one report failing → whole result fails.
        var handler = Respond(
            (_, _) => Success(UsageTodayOkSinglePage),
            (_, _) => Status(HttpStatusCode.Forbidden));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.Equal(KeyRejectedMessage, ex.Message);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_Usage404_MessageIsVerbatimE9()
    {
#if R019_IMPLEMENTED
        var handler = Respond(
            (_, _) => Status(HttpStatusCode.NotFound),
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        // E9 overrides C5's 404 row verbatim.
        Assert.Equal(AdminApiNotAvailableMessage, ex.Message);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_Cost404_MessageIsVerbatimE9()
    {
#if R019_IMPLEMENTED
        var handler = Respond(
            (_, _) => Success(UsageTodayOkSinglePage),
            (_, _) => Status(HttpStatusCode.NotFound));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.Equal(AdminApiNotAvailableMessage, ex.Message);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_429WithRetryAfter_MessageIsFixedC5()
    {
#if R019_IMPLEMENTED
        var handler = Respond(
            (_, _) =>
            {
                var response = Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return response;
            },
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        // C5: the 429 message is the fixed short one (unlike the OAuth client's countdown — not required here).
        Assert.Equal(RateLimitedMessage, ex.Message);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_Server500WithEchoingBody_SafeShortMessageNoKeyNoBody()
    {
#if R019_IMPLEMENTED
        var handler = Respond(
            (_, _) => Status(
                HttpStatusCode.InternalServerError,
                $$"""{"error":{"type":"overloaded_error","detail":"key {{KeySentinel}} {{BodyEchoMarker}}"}}"""),
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        // C5/E8: short message for "other" statuses; never the key, headers, or response body.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BodyEchoMarker, ex.Message, StringComparison.Ordinal);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    // ---- E5: cursor/shape rules -------------------------------------------------------------

    [Fact]
    public async Task GetUsageAndCostAsync_HasMoreWithNullNextPage_FailsUnavailable()
    {
#if R019_IMPLEMENTED
        const string broken =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":true,"next_page":null}""";
        var handler = Respond((_, _) => Success(broken), (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        // E5: has_more true with a null cursor → the whole Admin result is Unavailable.
        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_HasMoreWithEmptyNextPage_FailsUnavailable()
    {
#if R019_IMPLEMENTED
        const string broken =
            """{"data":[],"has_more":true,"next_page":""}""";
        var handler = Respond((_, _) => Success(broken), (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_RepeatedCursor_StopsAndFails()
    {
#if R019_IMPLEMENTED
        // E5: a repeated next_page (server hands back the same cursor it was given) → Unavailable,
        // and the client must stop instead of looping.
        const string alwaysSame =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":true,"next_page":"cursor-1"}""";
        var usageSeen = 0;
        var handler = Respond(
            (_, index) =>
            {
                usageSeen++;
                return Success(alwaysSame);
            },
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.Equal(2, usageSeen); // page 1 + page 2 (sees the repeat), then stop
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_Page10StillHasMore_FailsAfterTenPages()
    {
#if R019_IMPLEMENTED
        // E5: at most 10 pages per report; page 10 still has_more → Unavailable.
        var usageSeen = 0;
        var handler = Respond(
            (_, index) =>
            {
                usageSeen++;
                return Success(UsagePageWithCursor(index, hasMore: true));
            },
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.Equal(10, usageSeen);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_UsageFailsAndCostSucceeds_WholeResultFails()
    {
#if R019_IMPLEMENTED
        // E5: "one report failing while the other succeeds → Unavailable" (both directions).
        var handler = Respond(
            (_, _) => Status(HttpStatusCode.InternalServerError),
            (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    // ---- E4/E5: required shapes → Unavailable, never zero -------------------------------------

    [Theory]
    [InlineData(
        // output_tokens missing
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0}}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // cache_creation object missing entirely
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
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
        // envelope "has_more" missing (E5 required shapes: data[], has_more, next_page)
        """{"data":[]}""")]
    [InlineData(
        // token value as a string instead of a number
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":"1","cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // token value beyond long range (unparsable)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":99999999999999999999,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null}""")]
    public async Task GetUsageAndCostAsync_MalformedUsagePayload_FailsNeverZero(string malformedJson)
    {
#if R019_IMPLEMENTED
        var handler = Respond((_, _) => Success(malformedJson), (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        // E4/E5: missing token fields / bad envelope → whole result Unavailable, with a fixed safe
        // message (E7) — never raw parse diagnostics, never zero.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
#else
        Assert.Fail(GuardrailNotMergedMessage() + " Malformed usage payload under test: " + malformedJson);
        await Task.CompletedTask;
#endif
    }

    [Theory]
    [InlineData(
        // negative amount
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"-5.00","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // non-USD currency (E4)
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"EUR"}]}],"has_more":false,"next_page":null}""")]
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
        // unparsable amount string
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"1e5","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    [InlineData(
        // amount with group separator — InvariantCulture decimal must reject it
        """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"1,234.50","currency":"USD"}]}],"has_more":false,"next_page":null}""")]
    public async Task GetUsageAndCostAsync_MalformedCostPayload_FailsNeverZero(string malformedJson)
    {
#if R019_IMPLEMENTED
        var handler = Respond((_, _) => Success(UsageTodayOkSinglePage), (_, _) => Success(malformedJson));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
#else
        Assert.Fail(GuardrailNotMergedMessage() + " Malformed cost payload under test: " + malformedJson);
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_TokenSumOverflowsLong_FailsNotWraps()
    {
#if R019_IMPLEMENTED
        // E4: token sums use checked long. Day15: output = long.MaxValue (everything else 0);
        // day16(today): uncached = 1 → the month sum would overflow → whole result Unavailable,
        // never a wrapped negative.
        const string page =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":0,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":9223372036854775807}]},{"starting_at":"2026-11-16T00:00:00Z","ending_at":"2026-11-17T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":0}]}],"has_more":false,"next_page":null}""";
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero));
        var handler = Respond((_, _) => Success(page), (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), clock);

        await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_EmptyMonthBuckets_SucceedsWithZeroes()
    {
#if R019_IMPLEMENTED
        // Legitimate zero (NOT the E4 failure zero): the API returned successfully with no usage in
        // the window. "Buckets oldest first, empty buckets have empty results" (Facts).
        const string emptyUsage = """{"data":[],"has_more":false,"next_page":null}""";
        const string emptyCost = """{"data":[],"has_more":false,"next_page":null}""";
        var handler = Respond((_, _) => Success(emptyUsage), (_, _) => Success(emptyCost));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var result = await client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None);

        Assert.Equal(0, result.MonthTokens);
        Assert.Equal(0, result.TodayTokens);
        Assert.Equal(0m, result.MonthCostUsd);
        Assert.Equal(0m, result.TodayCostUsd);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    // ---- E7: deadline, body cap, cancellation ----------------------------------------------------

    [Fact]
    public async Task GetUsageAndCostAsync_PerRequestTimeout_SafeFixedMessageNotCancellation()
    {
#if R019_IMPLEMENTED
        // C4 default is 15 s; inject a short timeout so the test stays fast (house pattern from the
        // OAuth client). A slow response must become a fixed safe failure message, not an
        // OperationCanceledException escaping to the caller (E7 distinguishes caller-cancellation).
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return Success(UsageTodayOkSinglePage);
        });
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler),
            new FakeClock(NowMidMonthUtc),
            timeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_OverLimitBody_FailsWithSafeMessage()
    {
#if R019_IMPLEMENTED
        // E7: response body ≤ 1 MB per page (bounded read like the OAuth client). Default 1 000 000;
        // inject a small cap. The oversized page carries the sentinel to prove nothing is echoed.
        const string todayPrefix =
            """{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":1,"cache_read_input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1}]}],"has_more":false,"next_page":null,"padding":""";
        var body = todayPrefix + new string('x', 4096) + KeySentinel + "\"}";
        var handler = Respond((_, _) => Success(body), (_, _) => Success(CostTodayAndYesterday));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler),
            new FakeClock(NowMidMonthUtc),
            maxResponseBodyBytes: 256);

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_AlreadyCancelledToken_ThrowsWithoutAnyRequest()
    {
#if R019_IMPLEMENTED
        var handler = RespondOk();
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // E7: caller cancellation propagates as OperationCanceledException and is never turned into
        // "Unavailable". Checked before any network access (house rule from the OAuth client).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetUsageAndCostAsync(KeySentinel, cts.Token));
        Assert.Equal(0, handler.CallCount);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_CallerCancelsMidRequest_PropagatesOperationCanceled()
    {
#if R019_IMPLEMENTED
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            token.Register(() => release.TrySetCanceled(token));
            entered.TrySetResult();
            return await release.Task;
        });
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));
        using var cts = new CancellationTokenSource();

        var pending = client.GetUsageAndCostAsync(KeySentinel, cts.Token);
        await entered.Task;
        cts.Cancel();

        // E7: never mapped to Unavailable — the caller sees OperationCanceledException (or a derived
        // TaskCanceledException) and nothing else.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetUsageAndCostAsync_TransportFailure_SafeMessageWithoutKeyOrUrlEcho()
    {
#if R019_IMPLEMENTED
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("connect failed to https://api.anthropic.com " + KeySentinel)));
        var client = new AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageClient(
            new HttpClient(handler), new FakeClock(NowMidMonthUtc));

        var ex = await Assert.ThrowsAsync<AIMonitor.Infrastructure.Providers.Claude.AnthropicAdminUsageException>(
            () => client.GetUsageAndCostAsync(KeySentinel, CancellationToken.None));

        // C5/E8: network failures get a short message; the inner exception's text (which here contains
        // the sentinel) must not be forwarded.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain(KeySentinel, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(KeySentinel, ex.ToString(), StringComparison.Ordinal);
#else
        Assert.Fail(GuardrailNotMergedMessage());
        await Task.CompletedTask;
#endif
    }

    /// <summary>Recording handler (test-local, still a fake <see cref="HttpMessageHandler"/>): keeps
    /// every usage/cost request so per-report first-page assertions can name the right request.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> UsageRequests { get; } = [];

        public List<HttpRequestMessage> CostRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.Contains("cost_report", StringComparison.Ordinal))
            {
                CostRequests.Add(request);
                return Task.FromResult(Success(CostTodayAndYesterday));
            }

            UsageRequests.Add(request);
            return Task.FromResult(Success(UsageTodayOkSinglePage));
        }
    }
}
