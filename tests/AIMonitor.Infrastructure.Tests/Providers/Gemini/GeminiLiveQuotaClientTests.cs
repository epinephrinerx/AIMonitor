using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMonitor.Domain;
using AIMonitor.Application.Providers;
using AIMonitor.Infrastructure.Providers.Gemini;
using AIMonitor.Infrastructure.Tests.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// End-to-end tests for <see cref="GeminiLiveQuotaClient"/>: credential resolution (via the real
/// <see cref="GeminiCredentialResolver"/> against synthetic temp-directory files) drives which bearer
/// path runs - a service account is exchanged for a token via a self-signed JWT, a Gemini CLI OAuth
/// token is used as-is - and Cloud Monitoring request counts are folded into a provider-neutral
/// <see cref="ProviderSnapshot"/> preserving partial-success semantics (PAR-004) and never inventing
/// data (PAR-013). All HTTP is a hand-written fake <see cref="HttpMessageHandler"/>; nothing here ever
/// touches the real network, the real <c>%USERPROFILE%</c>, or a real Google credential.
/// </summary>
[Trait("Category", "Contract")]
public sealed class GeminiLiveQuotaClientTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-gemini-live-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(FixedNow);

    public GeminiLiveQuotaClientTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private GeminiLiveQuotaClient CreateClient(
        HttpMessageHandler? handler = null,
        string? savedServiceAccountPath = null,
        string? projectOverride = null,
        Func<string, string?>? environmentVariableReader = null,
        TimeSpan? timeout = null,
        int? maxResponseBodyBytes = null) =>
        new(
            new HttpClient(handler ?? new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("no HTTP call expected"))),
            _directory,
            _clock,
            savedServiceAccountPath: savedServiceAccountPath,
            projectOverride: projectOverride,
            environmentVariableReader: environmentVariableReader ?? (_ => null),
            timeout: timeout ?? TimeSpan.FromSeconds(5),
            maxResponseBodyBytes: maxResponseBodyBytes,
            // Fixed regardless of the machine running the test - otherwise "today"'s local-day window
            // would depend on the timezone of whatever machine or CI runner executes this suite.
            localTimeZone: TimeZoneInfo.Utc);

    // -- synthetic credential fixtures --------------------------------------

    private static (string Pem, RSA Key) GenerateServiceAccountKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa);
    }

    private string WriteServiceAccountFile(
        string fileName, string clientEmail, string privateKeyPem, string? projectId = "my-project", string type = "service_account")
    {
        var path = Path.Combine(_directory, fileName);
        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["client_email"] = clientEmail,
            ["private_key"] = privateKeyPem,
        };
        if (projectId is not null)
        {
            payload["project_id"] = projectId;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
    }

    private void WriteGeminiCliLogin(string accessToken, double? expiryDateMs = null, string account = "cli-user@example.com")
    {
        var geminiHome = Path.Combine(_directory, ".gemini");
        Directory.CreateDirectory(geminiHome);
        var oauth = new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["scope"] = "https://www.googleapis.com/auth/cloud-platform",
        };
        if (expiryDateMs is double expiry)
        {
            oauth["expiry_date"] = expiry;
        }

        File.WriteAllText(Path.Combine(geminiHome, "oauth_creds.json"), JsonSerializer.Serialize(oauth));
        File.WriteAllText(
            Path.Combine(geminiHome, "google_accounts.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["active"] = account }));
    }

    // -- JSON response builders ---------------------------------------------

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string TokenPayload(string accessToken, int expiresIn = 3600) =>
        JsonSerializer.Serialize(new { access_token = accessToken, expires_in = expiresIn });

    private static string TimeSeriesPayload(params (string StartTimeIso, long Value)[] points)
    {
        var series = points.Select(p => new
        {
            points = new[]
            {
                new
                {
                    interval = new { startTime = p.StartTimeIso, endTime = p.StartTimeIso },
                    value = new { int64Value = p.Value.ToString(CultureInfo.InvariantCulture) },
                },
            },
        });
        return JsonSerializer.Serialize(new { timeSeries = series });
    }

    private static string EmptyTimeSeriesPayload() => """{"timeSeries":[]}""";

    private static string ProjectsPayload(params string[] projectIds) =>
        JsonSerializer.Serialize(new { projects = projectIds.Select(id => new { projectId = id }) });

    /// <summary>Routes a fake handler's response by host, matching the three Google APIs this client
    /// can call: token exchange, project discovery, and Cloud Monitoring.</summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Router(
        Func<HttpRequestMessage, HttpResponseMessage>? onToken = null,
        Func<HttpRequestMessage, HttpResponseMessage>? onProjects = null,
        Func<HttpRequestMessage, HttpResponseMessage>? onMonitoring = null) =>
        (request, _) =>
        {
            var host = request.RequestUri!.Host;
            HttpResponseMessage response = host switch
            {
                "oauth2.googleapis.com" => (onToken ?? (_ => throw new InvalidOperationException("unexpected token call")))(request),
                "cloudresourcemanager.googleapis.com" =>
                    (onProjects ?? (_ => throw new InvalidOperationException("unexpected project discovery call")))(request),
                "monitoring.googleapis.com" => (onMonitoring ?? (_ => throw new InvalidOperationException("unexpected monitoring call")))(request),
                _ => throw new InvalidOperationException($"unexpected host {host}"),
            };
            return Task.FromResult(response);
        };

    // -- detection integration ----------------------------------------------

    [Fact]
    public async Task GetSnapshotAsync_NoCredentialAnywhere_ReturnsNotConnectedWithoutAnyHttpCall()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("must not call"));
        var client = CreateClient(handler: handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.Equal(DetectionState.NotConnected, snapshot.Detection!.State);
        Assert.Null(snapshot.Error);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_LimitedGcloudUserLogin_ReturnsUnconfiguredWithoutErrorOrHttpCall()
    {
        var adcDirectory = Path.Combine(_directory, "AppData", "Roaming", "gcloud");
        Directory.CreateDirectory(adcDirectory);
        File.WriteAllText(
            Path.Combine(adcDirectory, "application_default_credentials.json"),
            """{"type":"authorized_user","account":"someone@example.invalid","refresh_token":"not-read"}""");
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("must not call"));
        var client = CreateClient(handler: handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.Equal(DetectionState.Limited, snapshot.Detection!.State);
        Assert.Null(snapshot.Error);
        Assert.NotEmpty(snapshot.SetupHint);
        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain("not-read", snapshot.Detection.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ExpiredGeminiCliLogin_ReturnsErrorWithoutHttpCall()
    {
        WriteGeminiCliLogin("stale-token", expiryDateMs: FixedNow.AddHours(-1).ToUnixTimeMilliseconds());
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("must not call"));
        var client = CreateClient(handler: handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.Equal(DetectionState.Expired, snapshot.Detection!.State);
        Assert.NotNull(snapshot.Error);
        // Matches the Python baseline: only a hard token/project-resolution failure is `Unauthorized`;
        // an Expired *detection* is surfaced purely through `Error`/`Detection`, never this flag.
        Assert.False(snapshot.Unauthorized);
        Assert.Equal(0, handler.CallCount);
    }

    // -- service-account token minting --------------------------------------

    [Fact]
    public async Task GetSnapshotAsync_ValidServiceAccount_SignsAssertionAndReturnsTodayMeter()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem);
        string? capturedAssertion = null;
        var handler = new FakeHttpMessageHandler(Router(
            onToken: request =>
            {
                capturedAssertion = ExtractFormValue(request, "assertion");
                Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", ExtractFormValue(request, "grant_type"));
                return JsonResponse(TokenPayload("minted-access-token"));
            },
            onMonitoring: _ => JsonResponse(TimeSeriesPayload(("2026-03-15T00:00:00Z", 7)))));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Single(snapshot.Meters);
        Assert.Equal("requests_today", snapshot.Meters[0].Key);
        Assert.Null(snapshot.Meters[0].Percent);
        Assert.Equal("7", snapshot.Meters[0].Detail);
        Assert.Contains("robot@example.iam.gserviceaccount.com", snapshot.Account, StringComparison.Ordinal);
        Assert.Contains("my-project", snapshot.Account, StringComparison.Ordinal);
        Assert.NotEmpty(snapshot.ValueNote);

        // The assertion is a real, verifiable RS256 JWT signed with this test's own key - proving the
        // signing path actually works end to end, not just that some string was sent.
        Assert.NotNull(capturedAssertion);
        VerifyAssertionSignature(capturedAssertion!, rsaKey);
        var claims = DecodeJwtPayload(capturedAssertion!);
        Assert.Equal("robot@example.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/monitoring.read", claims.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.GetProperty("aud").GetString());
    }

    [Fact]
    public async Task GetSnapshotAsync_ServiceAccountProjectFromKey_IsUsedWithoutDiscoveryCall()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem, projectId: "from-key");
        var handler = new FakeHttpMessageHandler(Router(
            onToken: _ => JsonResponse(TokenPayload("token")),
            onProjects: _ => throw new InvalidOperationException("must not discover a project when the key already has one"),
            onMonitoring: _ => JsonResponse(EmptyTimeSeriesPayload())));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Contains("from-key", snapshot.Account, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectOverride_TakesPriorityOverKeyProject()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem, projectId: "from-key");
        var handler = new FakeHttpMessageHandler(Router(
            onToken: _ => JsonResponse(TokenPayload("token")),
            onMonitoring: _ => JsonResponse(EmptyTimeSeriesPayload())));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath, projectOverride: "override-project");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Contains("override-project", snapshot.Account, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoProjectAnywhere_DiscoversFirstActiveProject()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem, projectId: null);
        var handler = new FakeHttpMessageHandler(Router(
            onToken: _ => JsonResponse(TokenPayload("token")),
            onProjects: _ => JsonResponse(ProjectsPayload("discovered-one", "discovered-two")),
            onMonitoring: _ => JsonResponse(EmptyTimeSeriesPayload())));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Contains("discovered-one", snapshot.Account, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoActiveProjectDiscovered_ReturnsSafeError()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem, projectId: null);
        var handler = new FakeHttpMessageHandler(Router(
            onToken: _ => JsonResponse(TokenPayload("token")),
            onProjects: _ => JsonResponse("""{"projects":[]}""")));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
        Assert.False(snapshot.Unauthorized);
    }

    [Fact]
    public async Task GetSnapshotAsync_KeyWithUnparsablePrivateKeyAtSignTime_ReturnsSafeErrorNeverThePemText()
    {
        var keyPath = WriteServiceAccountFile(
            "key.json", "robot@example.iam.gserviceaccount.com", "-----BEGIN PRIVATE KEY-----\nnot-real-key-material\n-----END PRIVATE KEY-----");
        var client = CreateClient(savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain("not-real-key-material", snapshot.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task GetSnapshotAsync_TokenEndpointErrorStatus_ReturnsSanitizedErrorNeverEchoingTheBody(
        HttpStatusCode statusCode, bool expectedUnauthorized)
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode) { Content = new StringContent("""{"error":{"message":"leaked-secret-detail"}}""") }));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Equal(expectedUnauthorized, snapshot.Unauthorized);
        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain("leaked-secret-detail", snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_TokenEndpointTransportFailure_ReturnsSafeError()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem);
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("DNS failure"));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_TokenEndpointMalformedJson_ReturnsSafeError()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{not-json")));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_TokenEndpointOversizedResponseBody_ReturnsSafeErrorWithoutUnboundedMemoryUse()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem);
        var oversized = new string('9', 2000);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(oversized)));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath, maxResponseBodyBytes: 100);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_TokenEndpointReturnsNoAccessToken_ReturnsUnauthorizedError()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("""{"expires_in":3600}""")));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.True(snapshot.Unauthorized);
    }

    // -- Gemini CLI OAuth path ------------------------------------------------

    [Fact]
    public async Task GetSnapshotAsync_ValidGeminiCliLogin_UsesTokenDirectlyWithoutCallingTokenEndpoint()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(
            onToken: _ => throw new InvalidOperationException("must not exchange a token for an OAuth login"),
            onMonitoring: request =>
            {
                Assert.Equal("Bearer cli-access-token", request.Headers.Authorization?.ToString());
                return JsonResponse(TimeSeriesPayload(("2026-03-15T00:00:00Z", 3)));
            }));
        var client = CreateClient(handler: handler, projectOverride: "cli-project");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal("3", snapshot.Meters[0].Detail);
        Assert.Contains("cli-user@example.com", snapshot.Account, StringComparison.Ordinal);
    }

    // -- history / partial success / no-invented-data ------------------------

    [Fact]
    public async Task GetSnapshotAsync_WithHistory_BuildsBucketsAndStatsFromServerWindow()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: request =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("alignmentPeriod=86400s", StringComparison.Ordinal))
            {
                return JsonResponse(TimeSeriesPayload(
                    ("2026-03-15T00:00:00Z", 5),
                    ("2026-03-14T00:00:00Z", 2)));
            }

            return JsonResponse(TimeSeriesPayload(("2026-03-15T00:00:00Z", 5)));
        }));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 3, includeHistory: true), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Null(snapshot.HistoryError);
        Assert.NotNull(snapshot.History);
        Assert.Equal(3, snapshot.History!.Buckets.Count);
        Assert.Contains(snapshot.History.Buckets, b => b.Day == new DateOnly(2026, 3, 15) && b.Total == 5);
        Assert.Contains(snapshot.History.Buckets, b => b.Day == new DateOnly(2026, 3, 14) && b.Total == 2);
        // The day with no data at all has an empty breakdown, never a fabricated {"Requests": 0} entry
        // (PAR-013: no invented data).
        var emptyDay = Assert.Single(snapshot.History.Buckets, b => b.Day == new DateOnly(2026, 3, 13));
        Assert.Empty(emptyDay.PerModel);
        Assert.Equal(0, emptyDay.Total);
        Assert.Contains(snapshot.Stats, s => s.Label == "Requests in range" && s.Value == "7");
        Assert.Contains(snapshot.Stats, s => s.Label == "Project" && s.Value == "proj");
    }

    [Fact]
    public async Task GetSnapshotAsync_HistoryFailureDoesNotHideSuccessfulTodayMeter()
    {
        WriteGeminiCliLogin("cli-access-token");
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: request =>
        {
            callCount++;
            // First Monitoring call is "today" (short window); fail only the second (the history
            // range) so the already-read Today meter must survive (PAR-004).
            if (callCount == 1)
            {
                return JsonResponse(TimeSeriesPayload(("2026-03-15T00:00:00Z", 9)));
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        Assert.Single(snapshot.Meters);
        Assert.Equal("9", snapshot.Meters[0].Detail);
        Assert.Null(snapshot.Error);
        Assert.NotNull(snapshot.HistoryError);
        Assert.True(snapshot.Ok, "a refresh with a live Today reading is a success even when history failed");
        Assert.Empty(snapshot.Stats);
        Assert.Null(snapshot.History);
    }

    [Fact]
    public async Task GetSnapshotAsync_TodayMeterNeverHasAPercentage()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: _ => JsonResponse(TimeSeriesPayload(("2026-03-15T00:00:00Z", 100)))));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Null(snapshot.Meters[0].Percent);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoUsageAtAll_ReportsZeroWithoutError()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: _ => JsonResponse(EmptyTimeSeriesPayload())));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal("0", snapshot.Meters[0].Detail);
    }

    [Fact]
    public async Task GetSnapshotAsync_MonitoringUnparseableTimestamp_SkipsThePointRatherThanGuessing()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: request =>
        {
            if (request.RequestUri!.Query.Contains("alignmentPeriod=86400s", StringComparison.Ordinal))
            {
                return JsonResponse("""{"timeSeries":[{"points":[{"interval":{"startTime":"not-a-timestamp"},"value":{"int64Value":"5"}}]}]}""");
            }

            return JsonResponse(EmptyTimeSeriesPayload());
        }));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.All(snapshot.History!.Buckets, bucket => Assert.Equal(0, bucket.Total));
    }

    // -- HTTP hygiene / cancellation / redaction ------------------------------

    [Fact]
    public async Task GetSnapshotAsync_NeverDisposesTheCallerOwnedHttpMessageHandler()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: _ => JsonResponse(EmptyTimeSeriesPayload())));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task GetSnapshotAsync_AlreadyCancelled_ThrowsWithoutAnyHttpCallOrFileAccess()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("key.json", "robot@example.iam.gserviceaccount.com", pem);
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("must not call"));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_MonitoringRequestTimesOut_ReturnsSafeErrorRatherThanHanging()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        });
        var client = CreateClient(handler: handler, projectOverride: "proj", timeout: TimeSpan.FromMilliseconds(50));

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
        Assert.Contains("timed out", snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotAsync_CallerCancelsDuringMonitoringCall_PropagatesCancellationNotATimeoutError()
    {
        WriteGeminiCliLogin("cli-access-token");
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        });
        var client = CreateClient(handler: handler, projectOverride: "proj", timeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token));
    }

    [Fact]
    public async Task GetSnapshotAsync_UnauthorizedServiceAccount_NeverLeaksTheKeyPathOrTokenInTheErrorMessage()
    {
        var (pem, rsaKey) = GenerateServiceAccountKey();
        using var _ = rsaKey;
        var keyPath = WriteServiceAccountFile("secret-key-file.json", "robot@example.iam.gserviceaccount.com", pem);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = CreateClient(handler: handler, savedServiceAccountPath: keyPath);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain(keyPath, snapshot.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(pem, snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NonPositiveMaxResponseBodyBytes_Throws()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeminiLiveQuotaClient(http, _directory, _clock, maxResponseBodyBytes: 0));
    }

    [Fact]
    public void Constructor_NonPositiveTimeout_Throws()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeminiLiveQuotaClient(http, _directory, _clock, timeout: TimeSpan.Zero));
    }

    [Fact]
    public async Task GetSnapshotAsync_MonitoringPagination_PreservesExactInt64Counts()
    {
        WriteGeminiCliLogin("cli-access-token");
        var requests = new List<Uri>();
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: request =>
        {
            requests.Add(request.RequestUri!);
            var fixture = request.RequestUri!.Query.Contains("pageToken=page-two", StringComparison.Ordinal)
                ? "monitoring-page-2.json"
                : "monitoring-page-1.json";
            return JsonResponse(ReadFixture(fixture));
        }));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal("9007199.3B", snapshot.Meters[0].Detail);
        Assert.Equal(2, requests.Count);
        Assert.Contains("pageToken=page-two", requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_MonitoringIncompleteResponse_ReturnsSafeError()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(
            onMonitoring: _ => JsonResponse(ReadFixture("monitoring-incomplete.json"))));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Contains("incomplete", snapshot.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic partial result", snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_RepeatedMonitoringCursor_ReturnsSafeError()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(
            onMonitoring: _ => JsonResponse("""{"timeSeries":[],"nextPageToken":"same"}""")));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Contains("pagination", snapshot.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotAsync_CountOverflow_ReturnsSafeError()
    {
        WriteGeminiCliLogin("cli-access-token");
        var payload = TimeSeriesPayload(
            ("2026-03-15T00:00:00Z", long.MaxValue),
            ("2026-03-15T01:00:00Z", 1));
        var handler = new FakeHttpMessageHandler(Router(onMonitoring: _ => JsonResponse(payload)));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Contains("too large", snapshot.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotAsync_ExcessHistoryRange_PreservesTodayMeter()
    {
        WriteGeminiCliLogin("cli-access-token");
        var handler = new FakeHttpMessageHandler(Router(
            onMonitoring: _ => JsonResponse(TimeSeriesPayload(("2026-03-15T00:00:00Z", 9)))));
        var client = CreateClient(handler: handler, projectOverride: "proj");

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(historyDays: 401, includeHistory: true), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal("9", snapshot.Meters[0].Detail);
        Assert.Null(snapshot.Error);
        Assert.Contains("400", snapshot.HistoryError!, StringComparison.Ordinal);
        Assert.Null(snapshot.History);
    }

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gemini", fileName));

    // -- helpers --------------------------------------------------------------

    private static string ExtractFormValue(HttpRequestMessage request, string key)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        foreach (var pair in body.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == key)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException($"form key '{key}' not found in body '{body}'");
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/').PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement;
    }

    private static void VerifyAssertionSignature(string jwt, RSA publicKey)
    {
        var parts = jwt.Split('.');
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Convert.FromBase64String(
            parts[2].Replace('-', '+').Replace('_', '/').PadRight(parts[2].Length + ((4 - (parts[2].Length % 4)) % 4), '='));
        Assert.True(publicKey.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
