using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AIMonitor.Application.Providers;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.OpenAi;
using AIMonitor.Infrastructure.Tests.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// End-to-end tests for <see cref="OpenAiLiveQuotaClient"/>: credential resolution drives which of the
/// two paths (Codex App Server, PAR-009; Admin API, PAR-010) runs, and both preserve
/// <see cref="ProviderSnapshot"/>'s partial-success semantics (PAR-004). The Codex path uses a
/// hand-written fake <see cref="ICodexAppServerLauncher"/>; the Admin path uses a hand-written fake
/// <see cref="HttpMessageHandler"/>. Neither ever touches a real process or the network.
/// </summary>
[Trait("Category", "Contract")]
public sealed class OpenAiLiveQuotaClientTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-openai-live-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(FixedNow);

    public OpenAiLiveQuotaClientTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string AuthPath => Path.Combine(_directory, "auth.json");

    private static string Jwt(Dictionary<string, object?> claims)
    {
        var json = JsonSerializer.Serialize(claims);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"header.{payload}.signature";
    }

    private void WriteOAuthAuth(string accountId, double? expEpochSeconds = null)
    {
        var claims = new Dictionary<string, object?>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object?> { ["chatgpt_account_id"] = accountId },
        };
        if (expEpochSeconds is double exp)
        {
            claims["exp"] = exp;
        }

        var tokens = new Dictionary<string, object?>
        {
            ["access_token"] = Jwt(claims),
            ["account_id"] = accountId,
        };
        File.WriteAllText(AuthPath, JsonSerializer.Serialize(new Dictionary<string, object?> { ["tokens"] = tokens }));
    }

    private sealed class FakeCodexAppServerLauncher(
        Func<OpenAiCredential, bool, CancellationToken, Task<CodexAppServerResult>> handler) : ICodexAppServerLauncher
    {
        public int CallCount { get; private set; }

        public Task<CodexAppServerResult> RunAsync(OpenAiCredential credential, bool includeHistory, CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(credential, includeHistory, cancellationToken);
        }
    }

    private OpenAiLiveQuotaClient CreateClient(
        HttpMessageHandler? handler = null,
        ICodexAppServerLauncher? codexLauncher = null,
        string? savedAdminKey = null,
        double? monthlyBudgetUsd = null,
        Func<string, string?>? environmentVariableReader = null,
        TimeSpan? timeout = null,
        int? maxResponseBodyBytes = null) =>
        new(
            new HttpClient(handler ?? new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("no HTTP call expected"))),
            _directory,
            _clock,
            savedAdminKey: savedAdminKey,
            monthlyBudgetUsd: monthlyBudgetUsd,
            codexHomeOverride: _directory,
            environmentVariableReader: environmentVariableReader ?? (_ => null),
            codexLauncher: codexLauncher,
            timeout: timeout ?? TimeSpan.FromSeconds(5),
            maxResponseBodyBytes: maxResponseBodyBytes,
            // Fixed regardless of the machine running the test - otherwise "today"/"month start" (and
            // FixedNow's own calendar day) would depend on the local timezone of whatever machine or
            // CI runner executes this suite.
            localTimeZone: TimeZoneInfo.Utc);

    // -- Codex path (PAR-009) ---------------------------------------------

    [Fact]
    public async Task GetSnapshotAsync_ValidCodexOAuth_ReturnsMetersAndAccount()
    {
        WriteOAuthAuth("acct-1");
        var launcher = new FakeCodexAppServerLauncher((_, _, _) => Task.FromResult(
            new CodexAppServerResult(JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":10}}}""").RootElement, null, null)));
        var client = CreateClient(codexLauncher: launcher);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Single(snapshot.Meters);
        Assert.Equal(DetectionState.Connected, snapshot.Detection!.State);
        Assert.Contains("Codex", snapshot.Account, StringComparison.Ordinal);
        Assert.NotEmpty(snapshot.ValueNote);
        Assert.Equal(1, launcher.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_ValidCodexOAuthWithHistory_AttachesHistoryAndStats()
    {
        WriteOAuthAuth("acct-1");
        var rateLimits = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":10}}}""").RootElement;
        var usage = JsonDocument.Parse(
            """{"summary":{"lifetimeTokens":500},"dailyUsageBuckets":[{"startDate":"2026-03-15","tokens":42}]}""").RootElement;
        var launcher = new FakeCodexAppServerLauncher((_, includeHistory, _) =>
            Task.FromResult(new CodexAppServerResult(rateLimits, includeHistory ? usage : null, null)));
        var client = CreateClient(codexLauncher: launcher);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 14, includeHistory: true), CancellationToken.None);

        Assert.NotNull(snapshot.History);
        Assert.Single(snapshot.History!.Buckets);
        Assert.Equal(42, snapshot.History.Buckets[0].Total);
        Assert.Contains(snapshot.Stats, stat => stat.Label == "Lifetime tokens");
    }

    [Fact]
    public async Task GetSnapshotAsync_ExpiredCodexOAuth_ReturnsUnauthorizedErrorWithoutCallingLauncher()
    {
        WriteOAuthAuth("acct-1", FixedNow.AddHours(-1).ToUnixTimeSeconds());
        var launcher = new FakeCodexAppServerLauncher((_, _, _) => throw new InvalidOperationException("must not be called"));
        var client = CreateClient(codexLauncher: launcher, savedAdminKey: "sk-admin-should-not-be-used");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.True(snapshot.Unauthorized);
        Assert.False(snapshot.Ok);
        Assert.Equal(DetectionState.Expired, snapshot.Detection!.State);
        Assert.Equal(0, launcher.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_CodexLoginMissingAccountId_ReturnsUnconfiguredWithoutCallingLauncher()
    {
        File.WriteAllText(
            AuthPath,
            JsonSerializer.Serialize(new
            {
                tokens = new { access_token = Jwt(new Dictionary<string, object?>()) },
            }));
        var launcher = new FakeCodexAppServerLauncher((_, _, _) => throw new InvalidOperationException("must not be called"));
        var client = CreateClient(codexLauncher: launcher);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.Equal(DetectionState.Limited, snapshot.Detection!.State);
        Assert.Equal(0, launcher.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_CodexLauncherThrowsUnauthorized_ReturnsUnauthorizedErrorSnapshot()
    {
        WriteOAuthAuth("acct-1");
        var launcher = new FakeCodexAppServerLauncher(
            (_, _, _) => throw new CodexAppServerException("Codex login expired or was rejected.", unauthorized: true));
        var client = CreateClient(codexLauncher: launcher);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.True(snapshot.Unauthorized);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_CodexReturnsNoQuotaWindows_IsAnErrorButHistoryStillShows()
    {
        WriteOAuthAuth("acct-1");
        var usage = JsonDocument.Parse("""{"dailyUsageBuckets":[{"startDate":"2026-03-15","tokens":7}]}""").RootElement;
        var launcher = new FakeCodexAppServerLauncher(
            (_, _, _) => Task.FromResult(new CodexAppServerResult(JsonDocument.Parse("{}").RootElement, usage, null)));
        var client = CreateClient(codexLauncher: launcher);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        Assert.Empty(snapshot.Meters);
        Assert.NotNull(snapshot.Error);
        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.History); // PAR-004: a quota failure must not suppress readable history
    }

    [Fact]
    public async Task GetSnapshotAsync_CodexHistoryErrorDoesNotHideSuccessfulQuota()
    {
        WriteOAuthAuth("acct-1");
        var rateLimits = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":10}}}""").RootElement;
        var launcher = new FakeCodexAppServerLauncher(
            (_, _, _) => Task.FromResult(new CodexAppServerResult(rateLimits, null, "Token history unavailable.")));
        var client = CreateClient(codexLauncher: launcher);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        Assert.Single(snapshot.Meters);
        Assert.Null(snapshot.Error);
        Assert.Equal("Token history unavailable.", snapshot.HistoryError);
        Assert.True(snapshot.Ok, "a refresh with live quota is a success even when history failed");
    }

    [Fact]
    public async Task GetSnapshotAsync_CodexOAuthNeverFallsBackToAdminApiEvenWithSavedAdminKey()
    {
        WriteOAuthAuth("acct-1");
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("Admin API must not be called"));
        var launcher = new FakeCodexAppServerLauncher((_, _, _) => Task.FromResult(
            new CodexAppServerResult(JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":10}}}""").RootElement, null, null)));
        var client = CreateClient(handler: handler, codexLauncher: launcher, savedAdminKey: "sk-admin-saved");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal(1, launcher.CallCount);
    }

    // -- Admin API path (PAR-010) ------------------------------------------

    private static string CostsPayload(double total, long startTimeUnixSeconds = 1_700_000_000) =>
        "{\"data\":[{\"start_time\":" + startTimeUnixSeconds.ToString(CultureInfo.InvariantCulture) +
        ",\"results\":[{\"amount\":{\"value\":" + total.ToString(CultureInfo.InvariantCulture) +
        ",\"currency\":\"usd\"}}]}],\"has_more\":false,\"next_page\":null}";

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> FixedCostsResponder(double total) =>
        (request, _) =>
        {
            Assert.Equal("/v1/organization/costs", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(CostsPayload(total)));
        };

    [Fact]
    public async Task GetSnapshotAsync_ValidAdminKey_ReturnsSpendMetersWithoutBudget()
    {
        var handler = new FakeHttpMessageHandler(FixedCostsResponder(20));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal(2, snapshot.Meters.Count);
        var monthMeter = Assert.Single(snapshot.Meters, m => m.Key == "month_spend");
        Assert.Null(monthMeter.Percent);
        Assert.Equal("$20.00", monthMeter.Detail);
        Assert.Equal(2, handler.CallCount); // month cost + today cost, no retry
    }

    [Fact]
    public async Task GetSnapshotAsync_ValidAdminKeyWithBudget_ShowsPercentMeter()
    {
        var handler = new FakeHttpMessageHandler(FixedCostsResponder(20));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test", monthlyBudgetUsd: 100);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        var monthMeter = Assert.Single(snapshot.Meters, m => m.Key == "month_budget");
        Assert.Equal(20, monthMeter.Percent);
    }

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

    [Fact]
    public async Task GetSnapshotAsync_MonthAndTodayCostsAreFetchedWithDifferentStartTimes()
    {
        var startTimes = new List<string>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            startTimes.Add(QueryParameter(request.RequestUri!, "start_time"));
            return Task.FromResult(JsonResponse(CostsPayload(1)));
        });
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Equal(2, startTimes.Count);
        Assert.NotEqual(startTimes[0], startTimes[1]);
    }

    [Fact]
    public async Task GetSnapshotAsync_OrdinaryProjectKey_ReturnsLimitedWithoutAnyHttpCall()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("must not call the Admin API"));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-project-not-admin");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.Equal(DetectionState.Limited, snapshot.Detection!.State);
        Assert.NotEmpty(snapshot.SetupHint);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoCredentialAtAll_ReturnsNotConnected()
    {
        var client = CreateClient();

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.Equal(DetectionState.NotConnected, snapshot.Detection!.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task GetSnapshotAsync_AdminApiErrorStatus_ReturnsSanitizedErrorNeverEchoingTheBody(
        HttpStatusCode statusCode, bool expectedUnauthorized)
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode) { Content = new StringContent("""{"error":{"message":"leaked-secret-detail"}}""") }));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Equal(expectedUnauthorized, snapshot.Unauthorized);
        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain("leaked-secret-detail", snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminApiTransportFailure_ReturnsSafeError()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("DNS failure"));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminApiMalformedJson_ReturnsSafeError()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{not-json")));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminApiOversizedResponseBody_ReturnsSafeErrorWithoutUnboundedMemoryUse()
    {
        var oversized = new string('9', 2000);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(oversized)));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test", maxResponseBodyBytes: 100);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminApiFailure_DoesNotRetry()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Equal(1, handler.CallCount); // fails on month cost; today cost/history are never attempted
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminApiHistoryFailure_DoesNotHideSpendMeters()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            callCount++;
            if (request.RequestUri!.AbsolutePath == "/v1/organization/costs" && callCount <= 2)
            {
                return Task.FromResult(JsonResponse(CostsPayload(5)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        var snapshot = await client.GetSnapshotAsync(new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        Assert.Equal(2, snapshot.Meters.Count);
        Assert.Null(snapshot.Error);
        Assert.NotNull(snapshot.HistoryError);
        Assert.True(snapshot.Ok);
    }

    [Fact]
    public async Task GetSnapshotAsync_NeverDisposesTheCallerOwnedHttpMessageHandler()
    {
        var handler = new FakeHttpMessageHandler(FixedCostsResponder(1));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task GetSnapshotAsync_AlreadyCancelled_ThrowsWithoutAnyHttpCall()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("must not call"));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_MoreThanEightModelsInRange_FoldsIntoOther()
    {
        var models = Enumerable.Range(1, 10).Select(i => $"model-{i}").ToArray();
        var results = string.Join(',', models.Select((m, i) =>
            $$"""{"model":"{{m}}","input_tokens":{{100 - i}},"output_tokens":0}"""));
        // Must fall within the queried history range (today and the 13 days before it, in UTC, per
        // CreateClient's fixed localTimeZone) or the bucket would never be associated with any day
        // this loop actually builds, and the fold-to-"Other" path would never be reached at all.
        var usagePayload = $$"""{"data":[{"start_time":{{FixedNow.ToUnixTimeSeconds()}},"results":[{{results}}]}]}""";

        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath == "/v1/organization/usage/completions" ? usagePayload : CostsPayload(0))));
        var client = CreateClient(handler: handler, savedAdminKey: "sk-admin-test");

        var snapshot = await client.GetSnapshotAsync(new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        Assert.NotNull(snapshot.History);
        Assert.Equal(8, snapshot.History!.Series.Count);
        Assert.Contains("Other", snapshot.History.Series);
    }
}
