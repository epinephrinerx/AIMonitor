using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Application's <see cref="IProviderQuotaClient"/> port for Claude, backed by Claude Code's own
/// OAuth usage endpoint — the same one Claude Code's own <c>/usage</c> command reads, so the numbers
/// are the authoritative server-side quota rather than a local estimate. Credentials are the ones
/// Claude Code already stored on this machine, read-only (see <see cref="IClaudeCredentialReader"/>);
/// this client never refreshes them and never retries a failed request automatically.
/// </summary>
public sealed class ClaudeLiveQuotaClient : IProviderQuotaClient
{
    private const string ProviderId = ClaudeDetection.ProviderId;
    private const string OAuthBeta = "oauth-2025-04-20";
    private const string UserAgentValue = "AIUsageMonitor/2.0";

    private const string SetupHint =
        "Sign in to Claude Code on this machine (run `claude` in a terminal). This app reads that " +
        "existing login read-only and never writes to it.";

    private const string ValueNote =
        "Equivalent API value at list price - a subscription is not billed per token.";

    private const string MissingCredentialsMessage =
        "No Claude Code login found. Sign in by running `claude` in a terminal, then refresh.";

    private const string ExpiredMessage =
        "The stored access token has expired. Start Claude Code to refresh your login, then refresh here.";

    private const string TimeoutMessage = "The Claude usage request timed out. Try again.";

    private const string TransportErrorMessage =
        "Could not reach api.anthropic.com. Check your network connection and try again.";

    private const string ResponseBodyErrorMessage =
        "Could not read the Claude usage response. Try again.";

    private const string GenericRateLimitMessage = "Anthropic is rate-limiting usage requests. Try again shortly.";

    private const string EquivalentValueStatLabel = "Equivalent API value";

    private const string UnexpectedHistoryErrorMessage =
        "Local Claude usage history could not be read. Quota gauges above are unaffected.";

    /// <summary>Beyond this, showing the server-supplied delta as a countdown is more confusing than
    /// useful, so a bounded generic message is used instead.</summary>
    private const long MaxDisplayableRetryAfterSeconds = 24 * 60 * 60;

    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The largest duration <see cref="CancellationTokenSource"/>'s timer-based constructor
    /// supports (<c>uint.MaxValue - 1</c> milliseconds); anything above this throws at the
    /// framework level anyway, so it is rejected here with an actionable message instead.</summary>
    private static readonly TimeSpan MaxSupportedTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private const int DefaultMaxResponseBodyBytes = 1_000_000;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly string? _claudeConfigDirOverride;
    private readonly string _userProfileDirectory;
    private readonly IClock _clock;
    private readonly IClaudeCredentialReader _credentialReader;
    private readonly TimeSpan _timeout;
    private readonly int _maxResponseBodyBytes;
    private readonly ClaudeTranscriptStore _transcriptStore;

    /// <summary>Serializes <see cref="ClaudeTranscriptStore.RefreshAsync"/> together with every query
    /// that reads its result for one <see cref="AttachHistoryAsync"/> call, so two overlapping
    /// <see cref="GetSnapshotAsync"/> calls on the same client never interleave reads/writes on the
    /// store's shared mutable state. Not disposed: this type never calls
    /// <see cref="SemaphoreSlim.AvailableWaitHandle"/>, so there is no underlying OS handle to leak,
    /// and this class otherwise owns no disposable resources (the <see cref="HttpClient"/> is
    /// caller-owned).</summary>
    private readonly SemaphoreSlim _transcriptGate = new(1, 1);

    /// <param name="httpClient">Caller-owned; this client sends requests through it but never
    /// disposes it.</param>
    /// <param name="claudeConfigDirOverride">The value of <c>CLAUDE_CONFIG_DIR</c>, if any — passed
    /// in explicitly rather than read from the environment here, so path resolution stays
    /// deterministic and testable.</param>
    /// <param name="userProfileDirectory">The current user's profile directory, used as the base for
    /// the default (non-override) credentials path.</param>
    /// <param name="credentialReader">Defaults to <see cref="ClaudeCredentialFileReader"/>; tests
    /// inject a hand-written fake (e.g. a delayed one) to exercise cancellation deterministically.</param>
    /// <param name="timeout">Overrides the 15-second production default; must be positive, finite,
    /// and within what <see cref="CancellationTokenSource"/> supports. Tests inject a short value to
    /// keep timeout tests fast.</param>
    /// <param name="maxResponseBodyBytes">Overrides the 1,000,000-byte production default cap on the
    /// response body; must be positive. Tests inject a small value to keep overflow tests fast.</param>
    /// <param name="transcriptTimeZone">The zone used to compute each transcript record's local
    /// calendar day (PAR-006). Defaults to <see cref="TimeZoneInfo.Local"/>; tests inject a fixed zone
    /// so day-boundary behavior does not depend on the machine running the test.</param>
    public ClaudeLiveQuotaClient(
        HttpClient httpClient,
        string? claudeConfigDirOverride,
        string userProfileDirectory,
        IClock clock,
        IClaudeCredentialReader? credentialReader = null,
        TimeSpan? timeout = null,
        int? maxResponseBodyBytes = null,
        TimeZoneInfo? transcriptTimeZone = null)
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
        _claudeConfigDirOverride = claudeConfigDirOverride;
        _userProfileDirectory = userProfileDirectory;
        _clock = clock;
        _credentialReader = credentialReader ?? new ClaudeCredentialFileReader();
        _timeout = ValidateTimeout(timeout ?? DefaultTimeout);
        _maxResponseBodyBytes = maxResponseBodyBytes ?? DefaultMaxResponseBodyBytes;

        var transcriptsRoot = ClaudeTranscriptPathResolver.Resolve(claudeConfigDirOverride, userProfileDirectory);
        _transcriptStore = new ClaudeTranscriptStore(transcriptsRoot, clock, transcriptTimeZone);
    }

    /// <summary>Test-only seam: lets a test substitute a controlled stream in place of the real file
    /// open used by the internal transcript store, to exercise overlapping-refresh concurrency and
    /// its interaction with cancellation deterministically instead of racing real file I/O timing.
    /// Production always uses the public constructor, which reads real files.</summary>
    internal ClaudeLiveQuotaClient(
        HttpClient httpClient,
        string? claudeConfigDirOverride,
        string userProfileDirectory,
        IClock clock,
        Func<string, Stream> transcriptOpenFile,
        IClaudeCredentialReader? credentialReader = null,
        TimeSpan? timeout = null,
        int? maxResponseBodyBytes = null,
        TimeZoneInfo? transcriptTimeZone = null)
        : this(
            httpClient, claudeConfigDirOverride, userProfileDirectory, clock,
            credentialReader, timeout, maxResponseBodyBytes, transcriptTimeZone)
    {
        ArgumentNullException.ThrowIfNull(transcriptOpenFile);

        var transcriptsRoot = ClaudeTranscriptPathResolver.Resolve(claudeConfigDirOverride, userProfileDirectory);
        _transcriptStore = new ClaudeTranscriptStore(transcriptsRoot, clock, transcriptTimeZone, transcriptOpenFile);
    }

    /// <summary>Rejects zero, <see cref="Timeout.InfiniteTimeSpan"/>, any other negative value, and
    /// anything exceeding what <see cref="CancellationTokenSource"/> supports — only a positive,
    /// finite, supported duration is accepted.</summary>
    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "Timeout must be a positive duration.");
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

        var credentialsPath = ClaudeCredentialPathResolver.Resolve(_claudeConfigDirOverride, _userProfileDirectory);
        var credentialResult = await _credentialReader
            .ReadAsync(credentialsPath, _clock, cancellationToken)
            .ConfigureAwait(false);
        var detection = ClaudeDetection.Detect(credentialResult);

        ProviderSnapshot quotaSnapshot;
        if (credentialResult.Status is ClaudeCredentialStatus.Missing or ClaudeCredentialStatus.Invalid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            quotaSnapshot = new ProviderSnapshot(
                ProviderId,
                configured: false,
                unauthorized: true,
                error: MissingCredentialsMessage,
                setupHint: SetupHint,
                detection: detection);
        }
        else if (credentialResult.Status == ClaudeCredentialStatus.Expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            quotaSnapshot = new ProviderSnapshot(
                ProviderId,
                configured: true,
                unauthorized: true,
                error: ExpiredMessage,
                setupHint: SetupHint,
                detection: detection);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            quotaSnapshot = await FetchUsageAsync(credentialResult.Credentials!.AccessToken, detection, cancellationToken)
                .ConfigureAwait(false);
        }

        // Local transcript history has nothing to do with the OAuth token above - Claude Code writes
        // it regardless of login state - so it is read in every branch above, not only on quota
        // success (PAR-004: a history-only failure must never fail a successful live quota, and the
        // converse holds too: a quota failure must not suppress history that is otherwise readable).
        if (!request.IncludeHistory)
        {
            return quotaSnapshot;
        }

        return await AttachHistoryAsync(quotaSnapshot, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot> AttachHistoryAsync(
        ProviderSnapshot quotaSnapshot, ProviderSnapshotRequest request, CancellationToken cancellationToken)
    {
        string? historyError;
        UsageHistory? history = null;
        IReadOnlyList<Stat> stats = quotaSnapshot.Stats;

        // Acquired before the refresh and released only after every query below has run, so this
        // client's refresh-then-query sequence for one call never interleaves with another
        // overlapping call's on the same (shared, mutable) transcript store. Left outside the try
        // below on purpose: a cancellation while waiting here must propagate without attempting a
        // release that was never paired with an acquire.
        await _transcriptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transcriptStore.RefreshAsync(cancellationToken).ConfigureAwait(false);

            historyError = TranscriptHistoryNoteFormatter.Format(
                _transcriptStore.LastDirectoryError, _transcriptStore.MalformedRecordCount, _transcriptStore.FileReadFailureCount);

            // A directory-level failure means the scan never reached the per-file stage: nothing was
            // actually read, so History must stay absent (not an invented empty range) and no
            // equivalent-value stat is fabricated for it. A directory that was successfully scanned
            // and simply has nothing in it still produces a real, valid empty history/$0.00 below.
            if (_transcriptStore.LastDirectoryError is null)
            {
                history = _transcriptStore.BuildHistory(request.HistoryDays, request.Metric);

                var totals = _transcriptStore.WindowTotals(request.HistoryDays);
                var caveat = UsageValueCaveatFormatter.Format(
                    totals.EstimatedTokens,
                    _transcriptStore.EstimatedInRange(request.HistoryDays),
                    totals.UnpricedTokens,
                    _transcriptStore.UnpricedInRange(request.HistoryDays));
                stats = [new Stat(EquivalentValueStatLabel, FormatUsd(totals.CostUsd), caveat)];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A local-transcript failure must never take the live quota gauges with it (PAR-004).
            // ClaudeTranscriptStore already turns every expected failure (missing directory, bad
            // line, bad record) into LastDirectoryError/MalformedRecordCount/FileReadFailureCount
            // rather than throwing; this is a defensive net against anything unanticipated, matching
            // the same reasoning that store applies internally around each individual record.
            historyError = UnexpectedHistoryErrorMessage;
        }
        finally
        {
            _transcriptGate.Release();
        }

        return new ProviderSnapshot(
            quotaSnapshot.ProviderId,
            configured: quotaSnapshot.Configured,
            meters: quotaSnapshot.Meters,
            stats: stats,
            history: history,
            account: quotaSnapshot.Account,
            error: quotaSnapshot.Error,
            historyError: historyError,
            unauthorized: quotaSnapshot.Unauthorized,
            setupHint: quotaSnapshot.SetupHint,
            valueNote: quotaSnapshot.ValueNote,
            fetchedAt: quotaSnapshot.FetchedAt,
            detection: quotaSnapshot.Detection);
    }

    private static string FormatUsd(decimal amountUsd) => "$" + amountUsd.ToString("N2", CultureInfo.InvariantCulture);

    private async Task<ProviderSnapshot> FetchUsageAsync(
        string accessToken, DetectionInfo detection, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        using (var httpRequest = BuildRequest(accessToken))
        {
            try
            {
                response = await _httpClient
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The linked token fired from the timeout side, not the caller's — a safe error
                // snapshot, not a propagated cancellation. Rechecked in case the caller cancelled in
                // the narrow window between the exception and this line.
                cancellationToken.ThrowIfCancellationRequested();
                return ErrorSnapshot(detection, TimeoutMessage);
            }
            catch (HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ErrorSnapshot(detection, TransportErrorMessage);
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return HandleErrorResponse(response, detection);
            }

            string body;
            try
            {
                body = await ReadResponseBodyAsync(response, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ErrorSnapshot(detection, TimeoutMessage);
            }
            catch (ClaudeResponseBodyException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ErrorSnapshot(detection, ResponseBodyErrorMessage);
            }

            IReadOnlyList<Meter> meters;
            try
            {
                meters = ClaudeQuotaParser.Parse(body);
            }
            catch (ClaudeQuotaParseException ex)
            {
                // The parser's own message is already a fixed, safe string — never payload content.
                cancellationToken.ThrowIfCancellationRequested();
                return ErrorSnapshot(detection, ex.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderSnapshot(
                ProviderId,
                configured: true,
                meters: meters,
                fetchedAt: _clock.UtcNow,
                detection: detection,
                setupHint: SetupHint,
                valueNote: ValueNote);
        }
    }

    /// <summary>Marker for a response-body failure that must become a fixed safe snapshot: never
    /// carries the offending bytes/text, so it is safe to catch broadly at the call site.</summary>
    private sealed class ClaudeResponseBodyException : Exception;

    /// <summary>
    /// Reads the response body from a stream with a byte cap enforced independent of
    /// <c>Content-Length</c> (which may be absent or understated), then decodes it with a strict
    /// UTF-8 decoder. <see cref="IOException"/>, <see cref="InvalidOperationException"/> (e.g. an
    /// unsupported charset resolving to no decoder), <see cref="HttpRequestException"/> (a transport
    /// failure surfacing mid-read rather than at the initial send), a decoder fallback, and a cap
    /// overflow all become <see cref="ClaudeResponseBodyException"/> — never the raw bytes or
    /// exception text.
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
            throw new ClaudeResponseBodyException();
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
                        throw new ClaudeResponseBodyException();
                    }

                    buffer.Write(chunk, 0, bytesRead);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
            {
                throw new ClaudeResponseBodyException();
            }

            try
            {
                return StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            catch (DecoderFallbackException)
            {
                throw new ClaudeResponseBodyException();
            }
        }
    }

    private static HttpRequestMessage BuildRequest(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.TryParseAdd(UserAgentValue);
        return request;
    }

    private static ProviderSnapshot HandleErrorResponse(HttpResponseMessage response, DetectionInfo detection)
    {
        var statusCode = (int)response.StatusCode;

        if (statusCode is 401 or 403)
        {
            return ErrorSnapshot(
                detection,
                $"Claude rejected the stored access token (HTTP {statusCode}). Start Claude Code to " +
                "refresh your login, then refresh here.",
                unauthorized: true);
        }

        if (statusCode == 429)
        {
            return ErrorSnapshot(detection, RateLimitMessage(response.Headers.RetryAfter));
        }

        // No response body is ever echoed here — an arbitrary server-provided message is untrusted.
        return ErrorSnapshot(detection, $"Claude usage request failed (HTTP {statusCode}).");
    }

    /// <summary>
    /// Formats the <c>Retry-After</c> delta safely: zero is shown explicitly, a positive fractional
    /// value is rounded up (so "try again in Ns" is never reached before N seconds have actually
    /// passed), and the cast to <see cref="long"/> only ever happens after the value has been bounded
    /// below <see cref="MaxDisplayableRetryAfterSeconds"/> so it can never overflow or narrow
    /// unexpectedly. A missing or negative delta falls back to a generic message.
    /// </summary>
    private static string RateLimitMessage(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is not TimeSpan delta || delta < TimeSpan.Zero)
        {
            return GenericRateLimitMessage;
        }

        var wholeSecondsCeiling = Math.Ceiling(delta.TotalSeconds);
        if (double.IsNaN(wholeSecondsCeiling) || wholeSecondsCeiling > MaxDisplayableRetryAfterSeconds)
        {
            return "Anthropic is rate-limiting usage requests. Try again later.";
        }

        var wholeSeconds = (long)wholeSecondsCeiling;
        return $"Anthropic is rate-limiting usage requests. Try again in {wholeSeconds}s.";
    }

    private static ProviderSnapshot ErrorSnapshot(DetectionInfo detection, string error, bool unauthorized = false) =>
        new(
            ProviderId,
            configured: true,
            unauthorized: unauthorized,
            error: error,
            setupHint: SetupHint,
            detection: detection);
}
