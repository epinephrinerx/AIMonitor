using System.Globalization;
using System.Net;
using AIMonitor.Application.Providers;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// R-019 (written from Docs/SPEC_R019.md alone): C6 + decisions E1, E2, E6, E7, E8, E10 — the Claude
/// snapshot composition that calls <c>AnthropicAdminUsageClient</c> when a saved Claude Admin key
/// exists. Fake <see cref="HttpMessageHandler"/>s and a disposable temp directory only; fixed UTC
/// clock; InvariantCulture everywhere.
///
/// <para><b>Expected API (alignment contract for the Coding Owner after merge).</b> The spec fixes
/// only the type name <c>AnthropicAdminUsageClient</c> (in
/// <c>AIMonitor.Infrastructure.Providers.Claude</c>) and the existence of ONE explicit admin-only
/// signal; everything else below is the test author's best-shape guess:</para>
/// <code>
/// // C4 client — the same contract as AnthropicAdminUsageClientR019Tests.h
/// //   (HttpClient, IClock, TimeSpan? timeout, int? maxResponseBodyBytes)
/// //   + GetUsageAndCostAsync(string apiKey, CancellationToken) → AnthropicAdminUsageResult
/// //   + AnthropicAdminUsageException (fixed safe Message on every C5/E4/E5/E7 error path).
///
/// // E6 — ClaudeLiveQuotaClient keeps ONE instance across refreshes and reads the saved key at the
/// // start of every refresh through an injected reader (no key cached between refreshes). Guess:
/// // two new optional constructor parameters appended after the existing ones:
/// new ClaudeLiveQuotaClient(
///     httpClient, claudeConfigDirOverride, userProfileDirectory, clock,
///     credentialReader?, timeout?, maxResponseBodyBytes?, transcriptTimeZone?, xdgConfigHome?,
///     Func&lt;CancellationToken, Task&lt;string?&gt;&gt;? adminKeyReader = null,
///     AnthropicAdminUsageClient? adminClient = null)   // guess: injectable for tests; App builds it (C8)
///
/// // E2 — the one explicit signal. Guess: a new bool ProviderSnapshot property `AdminOnly`
/// // (false by default; true when the OAuth quota step produced no usable quota AND the Admin call
/// // succeeded). The tab rule (no setup card, Admin stats + the E2 note) keys on it. The E2 note
/// // text is asserted as "contained somewhere in the snapshot's texts" so the carrier member
/// // (ValueNote / Error / a new field) can be chosen by the Writing Owner.
/// </code>
///
/// <para>Stat strings (E10 verbatim): "API cost today (UTC)", "API cost this month (UTC)",
/// "API tokens this month (UTC)" with Detail "Anthropic Admin API · billed"; the failure stat is
/// "API usage" = "Unavailable" with the C5 message as Detail. Money display " $" + N2 invariant —
/// the existing Claude money formatter (1.2345m → "$1.23", C6/C4); tokens use the house compact
/// K/M rule ("15067 → 15.1K").</para>
///
/// <para><b>Why these tests fail on base 29f4048:</b> none of the above exists until the Writing
/// Owner's implementation merges; the guard below makes that a per-test failure. Activate the real
/// bodies with <c>-p:DefineConstants=R019_IMPLEMENTED</c> and align the guessed names.</para>
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeLiveQuotaClientAnthropicAdminR019Tests : IDisposable
{
    // Same clock as AnthropicAdminUsageClientR019Tests: "today" = 2026-11-15, month starts 2026-11-01.
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    private const string AdminKey = "sk-an…-b92e";
    private const string AdminKeyPrefixSentinel = "SENTINEL-ADMINKEY"; // the AdminKey contains this; sweeping for both is belt-and-braces

    // C5 401/403 message, verbatim — the expected Detail of the failure stat.
    private const string KeyRejectedDetail =
        "Admin key rejected — use an organization Admin key (sk-ant-admin…)";

    // E2 note, verbatim.
    private const string AdminOnlyNote =
        "Claude Code not signed in — quota unavailable; API usage from Admin key";

    // E10 stat labels, verbatim.
    private const string CostTodayLabel = "API cost today (UTC)";
    private const string CostMonthLabel = "API cost this month (UTC)";
    private const string TokensMonthLabel = "API tokens this month (UTC)";

    // Body content that must never reach the snapshot (C5/E8).
    private const string BodyEchoMarker = "SNAPSHOT-ECHO-MARKER-77aa";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-admin-r019-" + Guid.NewGuid().ToString("N"));

    public ClaudeLiveQuotaClientAnthropicAdminR019Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void WriteCredentials(DateTimeOffset expiresAtUtc)
    {
        var path = Path.Combine(_directory, ".credentials.json");
        var json = "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-oauth-token\",\"expiresAt\":"
            + expiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            + ",\"subscriptionType\":\"pro\"}}";
        File.WriteAllText(path, json);
    }

    // OAuth quota payload: one meter, so quota meters stay identifiable in every assertion below.
    private const string ValidUsagePayload = """{"limits":[{"kind":"session","percent":12.5,"severity":"normal"}]}""";

    // Admin usage, single page: today 7+11+13+17+19 = 67; yesterday 1000+2000+3000+4000+5000 = 15000
    // → MonthTokens = 15000 + 67 = 15067 (hand-computed; displayed compact as "15.1K").
    private const string AdminUsageOk =
        """
        {"data":[{"starting_at":"2026-11-14T00:00:00Z","ending_at":"2026-11-15T00:00:00Z","results":[{"model":"claude-sonnet-4-5","uncached_input_tokens":1000,"cache_read_input_tokens":2000,"cache_creation":{"ephemeral_5m_input_tokens":3000,"ephemeral_1h_input_tokens":4000},"output_tokens":5000}]},{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"claude-haiku-4-5","uncached_input_tokens":7,"cache_read_input_tokens":11,"cache_creation":{"ephemeral_5m_input_tokens":13,"ephemeral_1h_input_tokens":17},"output_tokens":19}]}],"has_more":false,"next_page":null}
        """;

    // Admin cost, single page: yesterday "100.00" + today "123.45" cents → today = 1.2345m,
    // month = 1.0000m + 1.2345m = 2.2345m (E4 exact; displayed "$1.23" / "$2.23").
    private const string AdminCostOk =
        """
        {"data":[{"starting_at":"2026-11-14T00:00:00Z","ending_at":"2026-11-15T00:00:00Z","results":[{"amount":"100.00","currency":"USD"}]},{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"amount":"123.45","currency":"USD"}]}],"has_more":false,"next_page":null}
        """;

    private static HttpResponseMessage Success(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>Hand-written fake OAuth transport: answers /api/oauth/usage, throws (test-visible)
    /// on anything else — the quota path must keep calling only its own endpoint (C6: OAuth behaviour
    /// never changes).</summary>
    private static FakeHttpMessageHandler OAuthHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? usageResponder = null) =>
        new((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!request.RequestUri!.AbsolutePath.Contains("oauth/usage", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected OAuth-path request {request.RequestUri} — the Claude quota path must "
                    + "only ever call /api/oauth/usage (C6).");
            }

            return Task.FromResult(usageResponder?.Invoke(request) ?? Success(ValidUsagePayload));
        });

    /// <summary>Hand-written fake Admin transport: records every request and answers the two report
    /// paths; failure modes are injected per path.</summary>
    private sealed class AdminRecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _failUsageWith;
        private readonly bool _failCost;

        public AdminRecordingHandler(HttpStatusCode? failUsageWith = null, bool failCost = false)
        {
            _failUsageWith = failUsageWith;
            _failCost = failCost;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var isCost = request.RequestUri!.AbsolutePath.Contains("cost_report", StringComparison.Ordinal);
            HttpResponseMessage response = !isCost && _failUsageWith is not null
                ? new HttpResponseMessage(_failUsageWith.Value)
                : isCost && _failCost
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Success(isCost ? AdminCostOk : AdminUsageOk);
            return Task.FromResult(response);
        }
    }

    /// <summary>Hand-written fake for the E6 injected key reader: serves the saved
    /// <c>providers/claude/key</c> value from an in-memory field, counts reads, supports changing the
    /// stored key between refreshes.</summary>
    private sealed class FakeAdminKeyReader
    {
        public string? Key { get; set; }

        public int Reads { get; private set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Key);
        }
    }

#if R019_IMPLEMENTED
    /// <summary>Builds the composed client exactly as the spec's E6/E8 composition intends, with the
    /// guessed optional constructor parameters (see the file header's alignment contract).</summary>
    private ClaudeLiveQuotaClient CreateAdminClient(
        HttpMessageHandler oauthHandler,
        FakeAdminKeyReader? keyReader = null,
        HttpMessageHandler? adminHttp = null,
        HttpStatusCode? adminUsageStatus = null,
        bool adminCostFails = false)
    {
        var reader = keyReader ?? new FakeAdminKeyReader { Key = AdminKey };
        var adminClient = new AnthropicAdminUsageClient(
            new HttpClient(adminHttp ?? new AdminRecordingHandler(adminUsageStatus, adminCostFails)),
            new FakeClock(FixedNow),
            timeout: TimeSpan.FromSeconds(5));
        return new ClaudeLiveQuotaClient(
            new HttpClient(oauthHandler),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            timeout: TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc,
            adminKeyReader: reader.ReadAsync,
            adminClient: adminClient);
    }

    private static Stat StatByName(ProviderSnapshot snapshot, string label) =>
        snapshot.Stats.Single(s => s.Label == label)!;

    private static string AllSnapshotText(ProviderSnapshot snapshot) => string.Join(
        "\n",
        [
            snapshot.ToString(),
            snapshot.Account,
            snapshot.Error ?? string.Empty,
            snapshot.HistoryError ?? string.Empty,
            snapshot.SetupHint,
            snapshot.ValueNote,
            .. snapshot.Stats.SelectMany(s => new[] { s.Label, s.Value, s.Detail }),
            .. snapshot.Meters.Select(m => m.ToString()),
            snapshot.Detection?.ToString() ?? string.Empty,
        ]);

    private static ProviderSnapshotRequest NoHistoryRequest() => ProviderSnapshotRequest.Default;
#else
    // The real composition (and the guessed E2/E6/E7 members) only exist after the R-019 merge; this
    // branch keeps the file compiling on base so the rest of the suite still runs.
    private ClaudeLiveQuotaClient CreateAdminClient(
        HttpMessageHandler oauthHandler,
        FakeAdminKeyReader? keyReader = null,
        HttpMessageHandler? adminHttp = null,
        HttpStatusCode? adminUsageStatus = null,
        bool adminCostFails = false) =>
        throw new NotSupportedException(R019ActivationMessage("ClaudeLiveQuotaClient admin composition"));
#endif

    private static string R019ActivationMessage(string what) =>
        "R-019 not on this branch (base 29f4048): " + what
        + " — AnthropicAdminUsageClient and the E2/E6 composition members do not exist here. Build with "
        + "-p:DefineConstants=R019_IMPLEMENTED after the Writing Owner's implementation merges and align "
        + "the guessed member names (see the file header).";

    // ---- C6 / E10 / E1: success appends exactly after the history step --------------------------

    [Fact]
    public async Task GetSnapshotAsync_OAuthOkAndAdminOk_AppendsThreeAdminStatsAfterEquivalentValue()
    {
#if R019_IMPLEMENTED
        // C6 + E1: stats appended AFTER the transcript step (which replaces Stats) — "Equivalent API
        // value" first, then the three Admin rows in C6's order; E10 labels/Detail verbatim.
        WriteCredentials(FixedNow.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_directory, "projects")); // scanned-but-empty history
        var oauth = OAuthHandler();
        var admin = new AdminRecordingHandler();
        var client = CreateAdminClient(oauth, adminHttp: admin);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        Assert.Equal("Equivalent API value", snapshot.Stats[0].Label);
        var adminStats = snapshot.Stats.Skip(1).ToArray();
        Assert.Equal(
            [CostTodayLabel, CostMonthLabel, TokensMonthLabel],
            adminStats.Select(s => s.Label).ToArray());

        // Oracles — fixtures above: today $1.2345 → "$1.23" (E4/C6 money formatter),
        // month $2.2345 → "$2.23", month tokens 15067 → "15.1K" (compact K/M like other token stats).
        Assert.Equal(["$1.23", "$2.23", "15.1K"], adminStats.Select(s => s.Value).ToArray());
        Assert.All(adminStats, s => Assert.Equal("Anthropic Admin API · billed", s.Detail));

        // Distinct from the transcript row (E10); no ccusage label collision.
        Assert.DoesNotContain("Equivalent API value", adminStats.Select(s => s.Label).ToArray());

        // C6: quota meters never change — the OAuth meter is exactly what OAuth alone produced.
        Assert.Single(snapshot.Meters);
        Assert.Equal("session", snapshot.Meters[0].Kind);
        Assert.True(snapshot.Ok);
        Assert.Null(snapshot.Error);
        Assert.Equal(1, oauth.CallCount);

        // Composition wiring (C4 via C6): one usage + one cost request, carrying the saved key.
        Assert.Equal(2, admin.Requests.Count);
        Assert.All(
            admin.Requests,
            r => Assert.Equal(AdminKey, r.Headers.GetValues("x-api-key").Single()));

        // E2 signal is only for admin-ONLY; with a working sign-in it must be false.
        Assert.False(snapshot.AdminOnly);
#else
        Assert.Fail(R019ActivationMessage("the OAuth+Admin success composition"));
        await Task.CompletedTask;
#endif
    }

    // ---- C6 failure: one "API usage · Unavailable" stat, quota output intact (E1) --------------

    [Fact]
    public async Task GetSnapshotAsync_AdminFailsOAuthOk_AddsOnlyUnavailableStatKeepsQuotaAndHistory()
    {
#if R019_IMPLEMENTED
        WriteCredentials(FixedNow.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_directory, "projects"));
        var client = CreateAdminClient(
            OAuthHandler(), adminUsageStatus: HttpStatusCode.Unauthorized);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        // E1: the history step's output survives; E10/C6: exactly ONE failure stat, no other C4 stats.
        Assert.Equal("Equivalent API value", snapshot.Stats[0].Label);
        var failed = Assert.Single(snapshot.Stats, s => s.Label != "Equivalent API value");
        Assert.Equal("API usage", failed.Label);
        Assert.Equal("Unavailable", failed.Value);

        // E10: Detail = the C5 message (401 row, verbatim).
        Assert.Equal(KeyRejectedDetail, failed.Detail);

        // C6: quota meters unchanged.
        Assert.Single(snapshot.Meters);
        Assert.True(snapshot.Ok);
        Assert.Null(snapshot.Error);
        Assert.False(snapshot.AdminOnly);
#else
        Assert.Fail(R019ActivationMessage("the OAuth-ok + Admin-fail failure stat"));
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetSnapshotAsync_CostFailsUsageOk_WholeResultFailsWithSingleUnavailableStat()
    {
#if R019_IMPLEMENTED
        // E5 cross-check at the composition level: "one report failing while the other succeeds →
        // Unavailable" — no half of the pair may be shown alone.
        WriteCredentials(FixedNow.AddHours(1));
        var client = CreateAdminClient(OAuthHandler(), adminCostFails: true);

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        var failed = Assert.Single(snapshot.Stats);
        Assert.Equal("API usage", failed.Label);
        Assert.Equal("Unavailable", failed.Value);
#else
        Assert.Fail(R019ActivationMessage("the usage-ok + cost-fail Unavailable rule (E5)"));
        await Task.CompletedTask;
#endif
    }

    // ---- E2: admin-only for OAuth missing / expired / 401, each × Admin success ------------------

    [Theory]
    [InlineData("missing")]      // no .credentials.json at all
    [InlineData("expired")]      // stored token expired before the fixed clock
    [InlineData("rejected401")]  // OAuth file present; /api/oauth/usage answers 401
    public async Task GetSnapshotAsync_NoUsableOAuthButAdminOk_ShowsThreeStatsFlagAndNoteNoSetupCard(
        string oauthCase)
    {
#if R019_IMPLEMENTED
        // E2 matrix, Admin-success leg. One explicit signal (guessed name: ProviderSnapshot.AdminOnly)
        // plus the note; detection stays truthful; no quota meters; C6: tab configured:true.
        if (oauthCase is "expired" or "rejected401")
        {
            WriteCredentials(oauthCase == "expired" ? FixedNow.AddHours(-1) : FixedNow.AddHours(1));
        }

        var oauthResponder = oauthCase == "rejected401"
            ? (Func<HttpRequestMessage, HttpResponseMessage>)
                (_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
            : null;
        var oauth = OAuthHandler(oauthResponder);
        var admin = new AdminRecordingHandler();
        var client = CreateAdminClient(oauth, adminHttp: admin);

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.Empty(snapshot.Meters);
        Assert.NotNull(StatByName(snapshot, CostTodayLabel));
        Assert.NotNull(StatByName(snapshot, CostMonthLabel));
        Assert.NotNull(StatByName(snapshot, TokensMonthLabel));

        // The E2 signal + note (note carrier member is the WO's choice; text is pinned).
        Assert.True(snapshot.AdminOnly);
        Assert.Contains(AdminOnlyNote, AllSnapshotText(snapshot), StringComparison.Ordinal);

        // E2: Claude Code detection stays truthful.
        Assert.NotNull(snapshot.Detection);
        Assert.Equal(
            oauthCase switch
            {
                "missing" => DetectionState.NotConnected,
                "expired" => DetectionState.Expired,
                _ => DetectionState.Connected, // file is valid; the rejection belongs to the quota path
            },
            snapshot.Detection.State);

        if (oauthCase == "missing")
        {
            // C6: with no sign-in but a working Admin key the usage call must not even be attempted.
            Assert.Equal(0, oauth.CallCount);
        }

        Assert.Equal(2, admin.Requests.Count);
#else
        Assert.Fail(R019ActivationMessage("the admin-only (E2) snapshot for OAuth case '" + oauthCase + "'"));
        await Task.CompletedTask;
#endif
    }

    // ---- E2 matrix, Admin-failure leg: today's behaviour + the Unavailable stat ------------------

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("rejected401")]
    public async Task GetSnapshotAsync_NoUsableOAuthAndAdminFails_KeepsTodayBehaviourPlusUnavailableStat(
        string oauthCase)
    {
#if R019_IMPLEMENTED
        // E2 explicitly requires testing the failure leg: "failure → today's behaviour + the
        // 'API usage · Unavailable' stat". "Today's behaviour" is compared against the same OAuth
        // scenario WITHOUT any Admin wiring (the base composition), so no private message text is
        // hardcoded here.
        if (oauthCase is "expired" or "rejected401")
        {
            WriteCredentials(oauthCase == "expired" ? FixedNow.AddHours(-1) : FixedNow.AddHours(1));
        }

        var oauthResponder = oauthCase == "rejected401"
            ? (Func<HttpRequestMessage, HttpResponseMessage>)
                (_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
            : null;

        // Baseline: plain OAuth-only client (the constructor's existing members only).
        var baselineOauth = OAuthHandler(oauthResponder);
        var baseline = new ClaudeLiveQuotaClient(
            new HttpClient(baselineOauth),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            timeout: TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc);
        var baselineSnapshot = await baseline.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        // Wired: same scenario, Admin key saved but the Admin call rejected (401).
        var wiredOauth = OAuthHandler(oauthResponder);
        var client = CreateAdminClient(
            wiredOauth, adminUsageStatus: HttpStatusCode.Unauthorized);
        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        // Today's behaviour preserved: same visible quota presentation as the baseline.
        Assert.Equal(baselineSnapshot.Configured, snapshot.Configured);
        Assert.Equal(baselineSnapshot.Unauthorized, snapshot.Unauthorized);
        Assert.Equal(baselineSnapshot.Error, snapshot.Error);
        Assert.Equal(baselineSnapshot.SetupHint, snapshot.SetupHint);
        Assert.Equal(
            baselineSnapshot.Meters.Select(m => m.Kind),
            snapshot.Meters.Select(m => m.Kind));
        Assert.Equal(baselineSnapshot.Detection!.State, snapshot.Detection!.State);

        // E2 signal must NOT be set when the Admin call failed, and the failure stat is present.
        Assert.False(snapshot.AdminOnly);
        var failed = Assert.Single(snapshot.Stats); // baseline stats are empty here (no history)
        Assert.Equal("API usage", failed.Label);
        Assert.Equal("Unavailable", failed.Value);
        Assert.Equal(KeyRejectedDetail, failed.Detail);
#else
        Assert.Fail(R019ActivationMessage("the Admin-fail leg of E2 for OAuth case '" + oauthCase + "'"));
        await Task.CompletedTask;
#endif
    }

    // ---- E6: one client, key read at the start of every refresh ---------------------------------

    [Fact]
    public async Task GetSnapshotAsync_NoSavedAdminKey_MakesNoAdminRequestAndStaysExactlyAsBefore()
    {
#if R019_IMPLEMENTED
        // C6: Admin is called only when a saved key exists. With no key, the snapshot is exactly the
        // OAuth-only result (R-006/R-008/R-011/R-014 unchanged — E10), while the key WAS read (E6).
        WriteCredentials(FixedNow.AddHours(1));
        var reader = new FakeAdminKeyReader { Key = null };
        var oauth = OAuthHandler();
        var client = CreateAdminClient(oauth, keyReader: reader);

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Empty(snapshot.Stats);
        Assert.Single(snapshot.Meters);
        Assert.False(snapshot.AdminOnly);
        Assert.Equal(1, oauth.CallCount);
        Assert.Equal(1, reader.Reads); // read at the start of the refresh; it just returned nothing
#else
        Assert.Fail(R019ActivationMessage("the no-saved-key path (C6) with E6 read counting"));
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetSnapshotAsync_ReadsSavedKeyAtStartOfEveryRefresh_NewestKeyGoesOnTheWire()
    {
#if R019_IMPLEMENTED
        // E6: no key cached between refreshes — changing the stored key takes effect on the very next
        // refresh of the SAME client instance (the Connect dialog's existing Save → refresh path, C8).
        WriteCredentials(FixedNow.AddHours(1));
        var reader = new FakeAdminKeyReader { Key = AdminKey };
        var admin = new AdminRecordingHandler();
        var client = CreateAdminClient(OAuthHandler(), keyReader: reader, adminHttp: admin);

        await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);
        const string replacedKey = "sk-an…-ffff";
        reader.Key = replacedKey;
        await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        Assert.Equal(2, reader.Reads);                       // once per refresh, never cached
        var wireKeys = admin.Requests.Select(r => r.Headers.GetValues("x-api-key").Single()).ToArray();
        Assert.Equal([AdminKey, AdminKey, replacedKey, replacedKey], wireKeys); // (usage+cost) × 2
#else
        Assert.Fail(R019ActivationMessage("E6 read-on-each-refresh"));
        await Task.CompletedTask;
#endif
    }

    // ---- E7: caller cancellation propagates, never becomes "Unavailable" -------------------------

    [Fact]
    public async Task GetSnapshotAsync_CallerCancelsDuringAdminCall_PropagatesOperationCanceled()
    {
#if R019_IMPLEMENTED
        WriteCredentials(FixedNow.AddHours(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangingAdmin = new FakeHttpMessageHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            return Success(AdminUsageOk);
        });
        var client = CreateAdminClient(OAuthHandler(), adminHttp: hangingAdmin);
        using var cts = new CancellationTokenSource();

        var pending = client.GetSnapshotAsync(NoHistoryRequest(), cts.Token);
        await entered.Task.ConfigureAwait(false);
        cts.Cancel();

        // E7: the caller sees OperationCanceledException — never a snapshot carrying
        // "API usage · Unavailable".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
#else
        Assert.Fail(R019ActivationMessage("E7 caller-cancellation propagation at the composition"));
        await Task.CompletedTask;
#endif
    }

    // ---- E8: sentinel sweep across every snapshot text --------------------------------------------

    [Fact]
    public async Task GetSnapshotAsync_AdminFailureSnapshotTexts_NeverContainSentinelKeyOrBody()
    {
#if R019_IMPLEMENTED
        WriteCredentials(FixedNow.AddHours(1));
        var echoingAdmin = new FakeHttpMessageHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    $$"""{"error":{"type":"authentication_error","message":"bad key {{AdminKey}} {{BodyEchoMarker}}"}}"""),
            });
        });
        var client = CreateAdminClient(OAuthHandler(), adminHttp: echoingAdmin);

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        // E8: the key, headers and response bodies never reach the snapshot — sweep every text the
        // tab/stats/detection/ToString could surface.
        var text = AllSnapshotText(snapshot);
        Assert.DoesNotContain(AdminKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminKeyPrefixSentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(BodyEchoMarker, text, StringComparison.Ordinal);

        // The failure stat still exists; its Detail is one of the fixed C5 messages, not the echo.
        var failed = StatByName(snapshot, "API usage");
        Assert.Equal("Unavailable", failed.Value);
        Assert.DoesNotContain(AdminKey, failed.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(BodyEchoMarker, failed.Detail, StringComparison.Ordinal);
#else
        Assert.Fail(R019ActivationMessage("the E8 sentinel sweep on Admin failure"));
        await Task.CompletedTask;
#endif
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminSuccessSnapshotTexts_NeverContainKeyOrHeaderNames()
    {
#if R019_IMPLEMENTED
        // E8 success side: the key travels only in request headers — nothing lands on the snapshot.
        WriteCredentials(FixedNow.AddHours(1));
        var client = CreateAdminClient(OAuthHandler(), adminHttp: new AdminRecordingHandler());

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        var text = AllSnapshotText(snapshot);
        Assert.DoesNotContain(AdminKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminKeyPrefixSentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain("x-api-key", text, StringComparison.OrdinalIgnoreCase);

        // Sanity that the sweep ran against real content: Equivalent + 3 Admin stats.
        Assert.Equal(4, snapshot.Stats.Count);
#else
        Assert.Fail(R019ActivationMessage("the E8 sentinel sweep on Admin success"));
        await Task.CompletedTask;
#endif
    }
}
