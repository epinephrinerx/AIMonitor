using System.Globalization;
using System.Net;
using AIMonitor.Application.Providers;
using AIMonitor.Infrastructure.Providers.OpenAi;
using AIMonitor.Infrastructure.Tests.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// OpenAI Admin API data-integrity contract tests: daily Usage/Costs pagination
/// (<c>limit</c>/<c>page</c>/<c>next_page</c>/<c>has_more</c>), required-shape validation (a malformed
/// response must become a sanitized error, never a silent zero), and the USD-currency check on every
/// cost amount before it is summed and formatted. Uses the sanitized golden fixtures under
/// <c>tests/Fixtures/OpenAi/</c> via a hand-written fake <see cref="HttpMessageHandler"/>; never touches
/// the network. Companion to <see cref="OpenAiLiveQuotaClientTests"/>, which covers credential
/// resolution and the non-pagination Admin API paths.
/// </summary>
[Trait("Category", "Contract")]
public sealed class OpenAiLiveQuotaClientDataIntegrityTests : IDisposable
{
    private const string UsagePath = "/v1/organization/usage/completions";
    private const string CostsPath = "/v1/organization/costs";
    private const string SanitizedErrorMessage = "Bad response from OpenAI. Try again.";

    private static readonly DateTimeOffset FixedNow = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly TodayLocal = new(2026, 3, 15);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-openai-data-integrity-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(FixedNow);

    public OpenAiLiveQuotaClientDataIntegrityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenAi", fileName);

    private static string ReadFixture(string fileName) => File.ReadAllText(FixturePath(fileName));

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string QueryParameter(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == name)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException($"Query parameter '{name}' was not present in '{uri}'.");
    }

    private OpenAiLiveQuotaClient CreateClient(HttpMessageHandler handler, string savedAdminKey = "sk-admin-test") =>
        new(
            new HttpClient(handler),
            _directory,
            _clock,
            savedAdminKey: savedAdminKey,
            codexHomeOverride: _directory,
            environmentVariableReader: _ => null,
            timeout: TimeSpan.FromSeconds(5),
            // Fixed regardless of the machine running the test, matching OpenAiLiveQuotaClientTests.
            localTimeZone: TimeZoneInfo.Utc);

    // -- Golden fixtures (happy path) --------------------------------------

    [Fact]
    public async Task GetSnapshotAsync_GoldenCostsAndUsageFixtures_ProducesCorrectMetersHistoryAndStats()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath == UsagePath ? ReadFixture("usage-daily.json") : ReadFixture("costs-daily.json"))));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 2, includeHistory: true), CancellationToken.None);

        Assert.True(snapshot.Ok);
        var monthMeter = Assert.Single(snapshot.Meters, m => m.Key == "month_spend");
        var todayMeter = Assert.Single(snapshot.Meters, m => m.Key == "today_spend");
        Assert.Equal("$19.75", monthMeter.Detail);
        Assert.Equal("$19.75", todayMeter.Detail);

        Assert.NotNull(snapshot.History);
        Assert.Equal(2, snapshot.History!.Buckets.Count);
        Assert.Equal(new DateOnly(2026, 3, 14), snapshot.History.Buckets[0].Day);
        Assert.Equal(2000, snapshot.History.Buckets[0].Total);
        Assert.Equal(new DateOnly(2026, 3, 15), snapshot.History.Buckets[1].Day);
        Assert.Equal(950, snapshot.History.Buckets[1].Total);
        Assert.Equal(new[] { "gpt-4o-mini", "gpt-4o" }, snapshot.History.Series);
        Assert.Equal(2, snapshot.History.ByModel.Count);
        Assert.Equal("gpt-4o-mini", snapshot.History.ByModel[0].Label);
        Assert.Equal(2150, snapshot.History.ByModel[0].Value);
        Assert.Equal("gpt-4o", snapshot.History.ByModel[1].Label);
        Assert.Equal(800, snapshot.History.ByModel[1].Value);

        Assert.Contains(snapshot.Stats, s => s.Label == "Spend in range" && s.Value == "$19.75");
        Assert.Contains(snapshot.Stats, s => s.Label == "Input tokens" && s.Value == "2,300");
        Assert.Contains(snapshot.Stats, s => s.Label == "Output tokens" && s.Value == "650");
        Assert.Contains(snapshot.Stats, s => s.Label == "Cached input" && s.Value == "150");
        Assert.Contains(snapshot.Stats, s => s.Label == "Requests" && s.Value == "11");
    }

    // -- Pagination completeness (has_more / next_page) --------------------

    [Fact]
    public async Task GetSnapshotAsync_UsagePaginationHasMore_FollowsNextPageAndSumsBothPages()
    {
        var usageCallCount = 0;
        Uri? secondUsageRequestUri = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == UsagePath)
            {
                usageCallCount++;
                if (usageCallCount == 2)
                {
                    secondUsageRequestUri = request.RequestUri;
                }

                return Task.FromResult(JsonResponse(ReadFixture(
                    usageCallCount == 1 ? "usage-paginated-page1.json" : "usage-paginated-page2.json")));
            }

            return Task.FromResult(JsonResponse(ReadFixture("costs-daily.json")));
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 2, includeHistory: true), CancellationToken.None);

        Assert.Equal(2, usageCallCount);
        Assert.Equal("usage-cursor-1", QueryParameter(secondUsageRequestUri!, "page"));
        Assert.Contains(snapshot.Stats, s => s.Label == "Input tokens" && s.Value == "1,000");
        Assert.Contains(snapshot.Stats, s => s.Label == "Output tokens" && s.Value == "300");
        Assert.Contains(snapshot.Stats, s => s.Label == "Requests" && s.Value == "8");
    }

    [Fact]
    public async Task GetSnapshotAsync_CostsPaginationHasMore_FollowsNextPageForEveryCostsCall()
    {
        var costsCallCount = 0;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == CostsPath)
            {
                costsCallCount++;
                // Each logical cost fetch (month, today, history) starts a fresh pagination cycle of
                // exactly two calls - odd calls are always that cycle's first page.
                return Task.FromResult(JsonResponse(ReadFixture(
                    costsCallCount % 2 == 1 ? "costs-paginated-page1.json" : "costs-paginated-page2.json")));
            }

            return Task.FromResult(JsonResponse(ReadFixture("usage-daily.json")));
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 2, includeHistory: true), CancellationToken.None);

        Assert.Equal(6, costsCallCount); // month + today + history costs, each a 2-page fetch
        var monthMeter = Assert.Single(snapshot.Meters, m => m.Key == "month_spend");
        var todayMeter = Assert.Single(snapshot.Meters, m => m.Key == "today_spend");
        Assert.Equal("$8.50", monthMeter.Detail);
        Assert.Equal("$8.50", todayMeter.Detail);
        Assert.Contains(snapshot.Stats, s => s.Label == "Spend in range" && s.Value == "$8.50");
    }

    // -- Required response shape: never a silent zero -----------------------

    [Fact]
    public async Task GetSnapshotAsync_UsageMissingDataShape_HistoryErrorIsSanitizedAndHistoryStaysNull()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath == UsagePath ? ReadFixture("missing-data-shape.json") : ReadFixture("costs-daily.json"))));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 2, includeHistory: true), CancellationToken.None);

        Assert.True(snapshot.Ok); // meters are a separate fetch and must not be hidden by a history failure (PAR-004)
        Assert.Null(snapshot.History);
        Assert.Empty(snapshot.Stats);
        Assert.Equal(SanitizedErrorMessage, snapshot.HistoryError);
        Assert.Equal(2, snapshot.Meters.Count);
    }

    [Fact]
    public async Task GetSnapshotAsync_CostsMissingDataShape_ReturnsSanitizedErrorWithNoMetersNeverZero()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(ReadFixture("missing-data-shape.json"))));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Equal(SanitizedErrorMessage, snapshot.Error);
        Assert.Empty(snapshot.Meters); // must never silently show a $0.00 spend meter instead
    }

    // -- Currency validation: never labeled USD unless it truly is ----------

    [Fact]
    public async Task GetSnapshotAsync_CostsNonUsdCurrency_ReturnsSanitizedErrorNotLabeledUsd()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(ReadFixture("costs-non-usd-currency.json"))));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Equal(SanitizedErrorMessage, snapshot.Error);
        Assert.DoesNotContain("eur", snapshot.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4.2", snapshot.Error, StringComparison.Ordinal);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_CostsMissingCurrency_ReturnsSanitizedErrorNotLabeledUsd()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(ReadFixture("costs-missing-currency.json"))));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Equal(SanitizedErrorMessage, snapshot.Error);
        Assert.Empty(snapshot.Meters);
    }

    // -- Requested-range clamp (1-90 days) and the <=31 per-page cap ---------

    [Fact]
    public async Task GetSnapshotAsync_HistoryRequestBeyond90Days_ClampsRangeAndCapsPageLimitAt31()
    {
        Uri? usageRequestUri = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == UsagePath)
            {
                usageRequestUri = request.RequestUri;
            }

            return Task.FromResult(JsonResponse("""{"object":"page","data":[],"has_more":false,"next_page":null}"""));
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 200, includeHistory: true), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.NotNull(snapshot.History);
        Assert.Equal(90, snapshot.History!.Days);
        Assert.Equal(90, snapshot.History.Buckets.Count);
        Assert.NotNull(usageRequestUri);
        Assert.Equal("31", QueryParameter(usageRequestUri!, "limit"));
        var expectedStartSeconds = new DateTimeOffset(TodayLocal.AddDays(-89).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .ToUnixTimeSeconds();
        Assert.Equal(expectedStartSeconds.ToString(CultureInfo.InvariantCulture), QueryParameter(usageRequestUri!, "start_time"));
    }

    // -- Bounded pages and loop detection ------------------------------------

    [Fact]
    public async Task GetSnapshotAsync_HasMoreTrueWithoutNextPage_HistoryErrorIsSanitized()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath == UsagePath
                ? """{"object":"page","data":[{"start_time":1773446400,"results":[]}],"has_more":true}"""
                : ReadFixture("costs-daily.json"))));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 2, includeHistory: true), CancellationToken.None);

        Assert.Null(snapshot.History);
        Assert.Equal(SanitizedErrorMessage, snapshot.HistoryError);
    }

    [Fact]
    public async Task GetSnapshotAsync_PaginationCursorRepeats_StopsInsteadOfLoopingForever()
    {
        var usageCallCount = 0;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == UsagePath)
            {
                usageCallCount++;
                return Task.FromResult(JsonResponse(
                    """{"object":"page","data":[],"has_more":true,"next_page":"loop-cursor"}"""));
            }

            return Task.FromResult(JsonResponse(ReadFixture("costs-daily.json")));
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 2, includeHistory: true), CancellationToken.None);

        Assert.Null(snapshot.History);
        Assert.Equal(SanitizedErrorMessage, snapshot.HistoryError);
        // First call learns the cursor; the repeat handed back on page 2 is caught immediately instead
        // of being followed indefinitely.
        Assert.Equal(2, usageCallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_PaginationNeverEnds_StopsAtMaxPagesInsteadOfLoopingForever()
    {
        var usageCallCount = 0;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == UsagePath)
            {
                usageCallCount++;
                return Task.FromResult(JsonResponse(
                    $$"""{"object":"page","data":[],"has_more":true,"next_page":"cursor-{{usageCallCount}}"}"""));
            }

            return Task.FromResult(JsonResponse(ReadFixture("costs-daily.json")));
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 2, includeHistory: true), CancellationToken.None);

        Assert.Null(snapshot.History);
        Assert.Equal(SanitizedErrorMessage, snapshot.HistoryError);
        Assert.Equal(10, usageCallCount); // bounded page count reached, even though has_more never turns false
    }
}
