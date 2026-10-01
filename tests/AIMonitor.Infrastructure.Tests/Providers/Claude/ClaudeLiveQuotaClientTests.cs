using System.Net;
using System.Net.Http.Headers;
using AIMonitor.Application.Providers;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Exercises <see cref="ClaudeLiveQuotaClient"/> end to end against a hand-written fake
/// <see cref="HttpMessageHandler"/> and synthetic credential files under a unique temp directory per
/// test. Never touches the real filesystem outside that directory, the real environment, or the
/// network.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeLiveQuotaClientTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-live-tests-" + Guid.NewGuid().ToString("N"));

    public ClaudeLiveQuotaClientTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CredentialsFilePath(params string[] extraSegments) =>
        Path.Combine([_directory, .. extraSegments, ".credentials.json"]);

    private void WriteCredentials(string json, params string[] extraSegments)
    {
        var path = CredentialsFilePath(extraSegments);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static string ValidCredentialsJson(DateTimeOffset expiresAtUtc) =>
        "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-token\",\"expiresAt\":"
        + expiresAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"subscriptionType\":\"pro\",\"rateLimitTier\":\"default_claude_pro\"}}";

    private const string ValidUsagePayload = """{"limits":[{"kind":"session","percent":12.5,"severity":"normal"}]}""";

    private static HttpResponseMessage SuccessResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private ClaudeLiveQuotaClient CreateClient(
        HttpMessageHandler handler,
        TimeSpan? timeout = null,
        bool useDefaultPath = false,
        string? userProfileDirectory = null,
        IClaudeCredentialReader? credentialReader = null,
        int? maxResponseBodyBytes = null) =>
        new(
            new HttpClient(handler),
            useDefaultPath ? null : _directory,
            userProfileDirectory ?? _directory,
            new FakeClock(FixedNow),
            credentialReader: credentialReader,
            timeout: timeout ?? TimeSpan.FromSeconds(5),
            maxResponseBodyBytes: maxResponseBodyBytes);

    [Fact]
    public async Task GetSnapshotAsync_ValidCredentialAndResponse_ReturnsConfiguredSnapshotWithMetersAndDetection()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.True(snapshot.Ok);
        Assert.Null(snapshot.Error);
        Assert.False(snapshot.Unauthorized);
        Assert.Single(snapshot.Meters);
        Assert.Equal("session", snapshot.Meters[0].Kind);
        Assert.Equal(FixedNow, snapshot.FetchedAt);
        Assert.NotNull(snapshot.Detection);
        Assert.Equal(DetectionState.Connected, snapshot.Detection!.State);
        Assert.NotEmpty(snapshot.SetupHint);
        Assert.NotEmpty(snapshot.ValueNote);
        Assert.Empty(snapshot.Stats);
        Assert.Null(snapshot.History);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_SendsExactlyOneRequestWithExpectedMethodUriAndHeaders()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://api.anthropic.com/api/oauth/usage"), request.RequestUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());
        Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
        Assert.Equal("AIUsageMonitor/2.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetSnapshotAsync_UsesDefaultPathUnderUserProfileWhenOverrideIsBlank()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)), ".claude");
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler, useDefaultPath: true);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_MissingCredentials_ReturnsUnconfiguredUnauthorizedSnapshotWithoutAnyHttpCall()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.True(snapshot.Unauthorized);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
        Assert.Equal(DetectionState.NotConnected, snapshot.Detection!.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_TokenlessCredentials_ReturnsUnconfiguredUnauthorizedSnapshotWithoutAnyHttpCall()
    {
        WriteCredentials("""{"claudeAiOauth":{"subscriptionType":"pro"}}""");
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.True(snapshot.Unauthorized);
        Assert.Equal(DetectionState.NotConnected, snapshot.Detection!.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_CorruptCredentialsFile_ReturnsUnconfiguredUnauthorizedSnapshotWithoutAnyHttpCall()
    {
        WriteCredentials("{not-json");
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Configured);
        Assert.True(snapshot.Unauthorized);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_ExpiredCredentials_ReturnsConfiguredUnauthorizedSnapshotWithoutAnyHttpCall()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddSeconds(-1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.True(snapshot.Unauthorized);
        Assert.Empty(snapshot.Meters);
        Assert.Equal(DetectionState.Expired, snapshot.Detection!.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_AlreadyCancelled_ThrowsWithoutAnyFileOrHttpAccess()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_CallerCancelsDuringTheHttpCall_PropagatesOperationCanceledException()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return SuccessResponse(ValidUsagePayload);
        });
        var client = CreateClient(handler, timeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token));
    }

    [Fact]
    public async Task GetSnapshotAsync_AdapterTimeoutElapses_ReturnsSafeErrorSnapshotRatherThanThrowing()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return SuccessResponse(ValidUsagePayload);
        });
        var client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(50));

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.False(snapshot.Unauthorized);
        Assert.Equal("The Claude usage request timed out. Try again.", snapshot.Error);
        Assert.Equal(DetectionState.Connected, snapshot.Detection!.State);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_Http401_ReturnsUnauthorizedWithActionableRefreshMessage()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.True(snapshot.Unauthorized);
        Assert.Contains("401", snapshot.Error);
        Assert.Contains("Claude Code", snapshot.Error);
        Assert.Equal(DetectionState.Connected, snapshot.Detection!.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_Http403_ReturnsUnauthorizedWithActionableRefreshMessage()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Unauthorized);
        Assert.Contains("403", snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_Http429WithValidRetryAfterDelta_UsesItInTheErrorMessage()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.False(snapshot.Unauthorized);
        Assert.Contains("Try again in 5s.", snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_Http429WithoutValidRetryAfter_FallsBackToGenericRateLimitMessage()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.TryAddWithoutValidation("Retry-After", "not-a-valid-delta");
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Contains("Try again shortly.", snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_OtherErrorStatus_NeverEchoesTheResponseBody()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        const string secretLikeBody = "SENTINEL_SERVER_BODY_UNSAFE_4b7d";
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(secretLikeBody),
            }));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.False(snapshot.Unauthorized);
        Assert.Contains("500", snapshot.Error);
        Assert.DoesNotContain(secretLikeBody, snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_TransportFailure_ReturnsSafeErrorSnapshot()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("DNS resolution failed for api.anthropic.com"));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.Equal(DetectionState.Connected, snapshot.Detection!.State);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_InvalidJsonPayload_ReturnsSafeErrorSnapshotRatherThanThrowing()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse("not-json")));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
        Assert.Equal(DetectionState.Connected, snapshot.Detection!.State);
    }

    [Fact]
    public async Task GetSnapshotAsync_ParserRejectsAnInvalidDomainValue_ReturnsSafeErrorSnapshot()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(SuccessResponse("""{"limits":[{"kind":"session","percent":-5}]}""")));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_NeverRetriesAutomaticallyAfterAFailure()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = CreateClient(handler);

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_DoesNotDisposeTheInjectedHttpClient()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        using var httpClient = new HttpClient(handler);
        var client = new ClaudeLiveQuotaClient(
            httpClient, _directory, _directory, new FakeClock(FixedNow), timeout: TimeSpan.FromSeconds(5));

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        using var probe = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        var disposalException = await Record.ExceptionAsync(() => httpClient.SendAsync(probe));
        Assert.Null(disposalException);
    }

    [Fact]
    public async Task GetSnapshotAsync_DoesNotDisposeTheInjectedHandler()
    {
        // Disposal is asserted via the handler's own flag rather than an indirect probe, proving
        // ownership explicitly for the underlying HttpMessageHandler as well as the HttpClient.
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task GetSnapshotAsync_DisposesTheResponseContentAfterReadingIt()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var content = new DisposalTrackingContent(ValidUsagePayload);
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var client = CreateClient(handler);

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(content.Disposed);
    }

    [Theory]
    [MemberData(nameof(InvalidTimeouts))]
    public void Constructor_InvalidTimeout_ThrowsArgumentOutOfRangeException(TimeSpan invalidTimeout)
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse("{}"))));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClaudeLiveQuotaClient(httpClient, _directory, _directory, new FakeClock(FixedNow), timeout: invalidTimeout));
    }

    public static TheoryData<TimeSpan> InvalidTimeouts() => new()
    {
        TimeSpan.Zero,
        Timeout.InfiniteTimeSpan,
        TimeSpan.FromMilliseconds(-5),
        TimeSpan.FromMilliseconds(uint.MaxValue),
    };

    [Fact]
    public void Constructor_PositiveFiniteSupportedTimeout_Succeeds()
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse("{}"))));

        var exception = Record.Exception(() =>
            new ClaudeLiveQuotaClient(httpClient, _directory, _directory, new FakeClock(FixedNow), timeout: TimeSpan.FromSeconds(15)));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveMaxResponseBodyBytes_ThrowsArgumentOutOfRangeException(int invalidCap)
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse("{}"))));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClaudeLiveQuotaClient(
                httpClient, _directory, _directory, new FakeClock(FixedNow), maxResponseBodyBytes: invalidCap));
    }

    [Fact]
    public async Task GetSnapshotAsync_CallerCancelsDuringCredentialRead_PropagatesOperationCanceledException()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler, credentialReader: new DelayedClaudeCredentialReader());
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_ResponseBodyExceedsConfiguredCap_ReturnsSafeErrorSnapshotRatherThanUnboundedRead()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var oversizedBody = "{\"limits\":[" + new string('0', 1000) + "]}";
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(oversizedBody)));
        var client = CreateClient(handler, maxResponseBodyBytes: 16);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
        Assert.DoesNotContain(oversizedBody, snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ResponseStreamThrowsIOException_ReturnsSafeErrorSnapshotRatherThanThrowing()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingContent() }));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_ResponseStreamThrowsHttpRequestException_ReturnsSafeErrorSnapshotRatherThanThrowing()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new HttpRequestExceptionThrowingContent() }));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_ResponseBodyIsInvalidUtf8_ReturnsSafeErrorSnapshotRatherThanThrowing()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        byte[] invalidUtf8 = [0xFF, 0xFE, 0x00, 0x01];
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(invalidUtf8) }));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public async Task GetSnapshotAsync_Http429WithZeroRetryAfter_RepresentsZeroExplicitly()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Contains("Try again in 0s.", snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_Http429WithFractionalRetryAfter_RoundsUpToWholeSeconds()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2.1));
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Contains("Try again in 3s.", snapshot.Error);
    }

    [Fact]
    public async Task GetSnapshotAsync_Http429WithExcessiveRetryAfter_UsesBoundedGenericMessage()
    {
        WriteCredentials(ValidCredentialsJson(FixedNow.AddHours(1)));
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(365));
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Contains("Try again later.", snapshot.Error);
        Assert.DoesNotContain("31536000", snapshot.Error, StringComparison.Ordinal);
    }

    /// <summary>Hand-written <see cref="HttpContent"/> whose read stream always fails with
    /// <see cref="IOException"/>, standing in for a broken transport/body without any real network.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ThrowingStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class ThrowingStream : Stream
        {
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

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new IOException("synthetic read failure");

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>Hand-written <see cref="HttpContent"/> whose read stream always fails with
    /// <see cref="HttpRequestException"/> from a chunk read (not the initial stream open), standing
    /// in for a transport failure that surfaces mid-body-read rather than at the initial send.</summary>
    private sealed class HttpRequestExceptionThrowingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ThrowingStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class ThrowingStream : Stream
        {
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

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new HttpRequestException("synthetic mid-read transport failure");

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
