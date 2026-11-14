using System.Globalization;
using System.Net;
using AIMonitor.Application.Providers;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// R-019 (Docs/SPEC_R019.md): C6 + decisions E1, E2, E6, E7, E8, E10, E11, E12 — the Claude snapshot
/// composition that calls <c>AnthropicAdminUsageClient</c> when a saved Claude Admin key exists.
/// Hand-written fake <see cref="HttpMessageHandler"/>s and a disposable temp directory only; fixed
/// UTC clock; InvariantCulture everywhere. No real network, key or clock.
///
/// <para><b>Aligned API (E11/E12: names/signatures read from the merged <c>src/</c>; behaviour still
/// comes from the spec — where an oracle and the code disagree, the spec decides and the mismatch is
/// reported, not bent):</b></para>
/// <code>
/// // E6/E11: ONE ClaudeLiveQuotaClient instance keeps its transcript store and reads the saved key
/// // at the start of every refresh through the appended optional parameter:
/// new ClaudeLiveQuotaClient(
///     httpClient, claudeConfigDirOverride, userProfileDirectory, clock,
///     credentialReader?, timeout?, maxResponseBodyBytes?, transcriptTimeZone?, xdgConfigHome?,
///     Func&lt;CancellationToken, Task&lt;string?&gt;&gt;? adminKeyReader = null)
/// // E11: there is NO injectable admin-client parameter — the composition builds
/// //   new AnthropicAdminUsageClient(httpClient, adminKey, clock) INTERNALLY on the same HttpClient,
/// //   so the fake below routes the OAuth endpoint and both Admin report paths through one handler.
/// // E2/E12a signal: ProviderSnapshot.AdminOnly — true only when the Admin call succeeded AND OAuth
/// //   is missing, expired or rejected (401/403). ProviderSnapshot.AdminApiConnected keeps meaning
/// //   "Admin succeeded", independent of the AdminOnly flag.
/// </code>
///
/// <para>Stat strings (E10 verbatim): "API cost today (UTC)", "API cost this month (UTC)",
/// "API tokens this month (UTC)" with Detail "Anthropic Admin API · billed"; the failure stat is
/// "API usage" = "Unavailable" with the C5 message as Detail. Money "$" + N2 invariant (1.2345m →
/// "$1.23"); tokens compact K/M/B with no "1000K" and ≥ 1e9 rendered "B" (E12f). The E2 note text
/// "Claude Code not signed in — quota unavailable; API usage from Admin key" is pinned as a string;
/// the carrier member is the Writing Owner's choice, so it is swept across the snapshot's texts.</para>
///
/// <para>E13 additions pinned below (fix round 2): the Admin-success matrix gains the OAuth-403 leg
/// (AdminOnly true); a healthy, unexpired sign-in whose quota call fails 500/429/timeout with a
/// working Admin key keeps AdminOnly FALSE and the OAuth error verbatim (compared against the same
/// scenario without Admin wiring); the compact boundaries 999,949 / 999,950 / 999,950,000 are added
/// to the K/M/B ladder; the fake Admin key now really contains the sweep marker and that
/// prerequisite is asserted before every sentinel sweep (Inspector B 4); and the old uncancellable
/// 150 ms cost delays are replaced by TaskCompletionSource-controlled waits (no wall-clock windows
/// left in this file).</para>
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeLiveQuotaClientAnthropicAdminR019Tests : IDisposable
{
    // Same clock as AnthropicAdminUsageClientR019Tests: "today" = 2026-11-15, month starts 2026-11-01.
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    // E13 (Inspector B 4): the fake Admin key MUST really contain the sentinel marker — the previous
    // key ("sk-an…-b92e") did NOT, so the AdminKeyPrefixSentinel checks in the sweeps below were
    // vacuous. The marker also doubles as the sk-ant-admin prefix probe (E8: no key prefix may
    // leak into any snapshot text either).
    private const string AdminKey = "sk-ant-admin-SENTINEL-ADMINKEY-8f2c-b92e";
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

    private static HttpResponseMessage Failure(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body) };

    /// <summary>Hand-written fake transport for the WHOLE composed client (E11: the Admin client is
    /// built internally on the same HttpClient): routes the OAuth usage endpoint and the two Admin
    /// report paths by URL, records every request per path, and answers per-path overrides. Never
    /// touches the network.</summary>
    private sealed class ClaudeAdminHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? OAuthResponder { get; init; }

        public Func<HttpRequestMessage, int, HttpResponseMessage>? AdminUsageResponder { get; init; }

        public Func<HttpRequestMessage, int, HttpResponseMessage>? AdminCostResponder { get; init; }

        /// <summary>When set, Admin requests hang until cancellation (caller-cancellation tests) and
        /// this source completes when the first Admin request arrives.</summary>
        public bool HangAdmin { get; init; }

        /// <summary>When set, the OAuth quota request hangs until the client's own deadline (or the
        /// caller) cancels it — the quota-timeout leg of the E13 healthy-OAuth matrix.</summary>
        public bool HangOAuth { get; init; }

        public TaskCompletionSource? AdminEntered { get; init; }

        public List<HttpRequestMessage> OAuthRequests { get; } = [];

        public List<HttpRequestMessage> AdminRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("oauth/usage", StringComparison.Ordinal))
            {
                // C6: the quota path keeps calling only /api/oauth/usage.
                OAuthRequests.Add(request);
                if (HangOAuth)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

                    // Only reached if the harness forgets to cancel: a normal quota answer would
                    // mask the timeout leg, so it must surface as a clear assertion miss instead.
                    return Success(ValidUsagePayload);
                }

                return OAuthResponder?.Invoke(request) ?? Success(ValidUsagePayload);
            }

            var isCost = path.Contains("cost_report", StringComparison.Ordinal);
            AdminRequests.Add(request);
            AdminEntered?.TrySetResult();

            if (HangAdmin)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

                // Only reached if the harness forgets to cancel: answer as an ordinary failure so a
                // wrong test shows up as a clear assertion miss, never as an unexpected exception.
                return Failure(HttpStatusCode.RequestTimeout);
            }

            if (isCost)
            {
                return AdminCostResponder?.Invoke(request, AdminRequests.Count) ?? Success(AdminCostOk);
            }

            return AdminUsageResponder?.Invoke(request, AdminRequests.Count) ?? Success(AdminUsageOk);
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

    /// <summary>Builds the composed client exactly as E6/E11 intends: one ClaudeLiveQuotaClient with
    /// the appended <c>adminKeyReader</c>; the Admin client is the implementation's own, on the same
    /// HttpClient, reading from the same clock. <paramref name="timeout"/> overrides the 5 s
    /// harness default so the quota-timeout leg stays fast (E13).</summary>
    private ClaudeLiveQuotaClient CreateAdminClient(
        ClaudeAdminHandler handler,
        FakeAdminKeyReader? keyReader = null,
        TimeSpan? timeout = null) =>
        new(
            new HttpClient(handler),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            timeout: timeout ?? TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc,
            adminKeyReader: (keyReader ?? new FakeAdminKeyReader { Key = AdminKey }).ReadAsync);

    /// <summary>The same client with NO Admin wiring at all (the pre-R-019 composition) — the
    /// "today's behaviour" baseline for the E2 failure legs.</summary>
    private ClaudeLiveQuotaClient CreateBaselineClient(
        ClaudeAdminHandler handler,
        TimeSpan? timeout = null) =>
        new(
            new HttpClient(handler),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            timeout: timeout ?? TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc);

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

    // ---- C6 / E10 / E1: success appends exactly after the history step --------------------------

    [Fact]
    public async Task GetSnapshotAsync_OAuthOkAndAdminOk_AppendsThreeAdminStatsAfterEquivalentValue()
    {
        // C6 + E1: stats appended AFTER the transcript step (which replaces Stats) — "Equivalent API
        // value" first, then the three Admin rows in C6's order; E10 labels/Detail verbatim.
        WriteCredentials(FixedNow.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_directory, "projects")); // scanned-but-empty history
        var handler = new ClaudeAdminHandler();
        var client = CreateAdminClient(handler);

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
        Assert.Single(handler.OAuthRequests);

        // Composition wiring (C4 via C6): one usage + one cost request, carrying the saved key.
        Assert.Equal(2, handler.AdminRequests.Count);
        Assert.All(
            handler.AdminRequests,
            r => Assert.Equal(AdminKey, r.Headers.GetValues("x-api-key").Single()));

        // E12a: AdminApiConnected = "Admin call succeeded"; the AdminOnly flag stays false because
        // OAuth is working — a signed-in Claude Code must not be repainted as admin-only.
        Assert.True(snapshot.AdminApiConnected);
        Assert.False(snapshot.AdminOnly);
    }

    // ---- C6 failure: one "API usage · Unavailable" stat, quota output intact (E1) --------------

    [Fact]
    public async Task GetSnapshotAsync_AdminFailsOAuthOk_AddsOnlyUnavailableStatKeepsQuotaAndHistory()
    {
        WriteCredentials(FixedNow.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_directory, "projects"));
        var client = CreateAdminClient(new ClaudeAdminHandler
        {
            AdminUsageResponder = (_, _) => Failure(HttpStatusCode.Unauthorized),
        });

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

        // E12a: "AdminApiConnected" still means "Admin call succeeded" — and this one did not.
        Assert.False(snapshot.AdminApiConnected);
    }

    [Fact]
    public async Task GetSnapshotAsync_CostFailsUsageOk_WholeResultFailsWithSingleUnavailableStat()
    {
        // E5 cross-check at the composition level: "one report failing while the other succeeds →
        // Unavailable" — no half of the pair may be shown alone.
        WriteCredentials(FixedNow.AddHours(1));
        var client = CreateAdminClient(new ClaudeAdminHandler
        {
            AdminCostResponder = (_, _) => Failure(HttpStatusCode.InternalServerError),
        });

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        var failed = Assert.Single(snapshot.Stats);
        Assert.Equal("API usage", failed.Label);
        Assert.Equal("Unavailable", failed.Value);
        Assert.False(snapshot.AdminApiConnected);
        Assert.False(snapshot.AdminOnly);
    }

    // ---- E2/E12a/E13: admin-only for OAuth missing / expired / 401 / 403, each × Admin ok --------

    [Theory]
    [InlineData("missing")]      // no .credentials.json at all
    [InlineData("expired")]      // stored token expired before the fixed clock
    [InlineData("rejected401")]  // OAuth file present; /api/oauth/usage answers 401
    [InlineData("rejected403")]  // E13 (handoff item 10): the 403 leg of the same "rejected" pair
    public async Task GetSnapshotAsync_NoUsableOAuthButAdminOk_ShowsThreeStatsFlagAndNoteNoSetupCard(
        string oauthCase)
    {
        // E2/E12a matrix, Admin-success leg: AdminOnly = true exactly when the Admin call succeeded
        // AND OAuth is missing, expired or rejected (401/403). Plus the note; detection stays
        // truthful; no quota meters; C6: configured:true.
        if (oauthCase is not "missing")
        {
            WriteCredentials(oauthCase == "expired" ? FixedNow.AddHours(-1) : FixedNow.AddHours(1));
        }

        var handler = new ClaudeAdminHandler
        {
            OAuthResponder = oauthCase switch
            {
                "rejected401" => _ => Failure(HttpStatusCode.Unauthorized),
                "rejected403" => _ => Failure(HttpStatusCode.Forbidden),
                _ => null,
            },
        };
        var client = CreateAdminClient(handler);

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Configured);
        Assert.Empty(snapshot.Meters);
        Assert.NotNull(StatByName(snapshot, CostTodayLabel));
        Assert.NotNull(StatByName(snapshot, CostMonthLabel));
        Assert.NotNull(StatByName(snapshot, TokensMonthLabel));

        // The E12a signal + note (note carrier member is the WO's choice; text is pinned).
        Assert.True(snapshot.AdminApiConnected); // Admin succeeded — this flag keeps that meaning.
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
            Assert.Empty(handler.OAuthRequests);
        }

        Assert.Equal(2, handler.AdminRequests.Count);
    }

    // ---- E2 matrix, Admin-failure leg: today's behaviour + the Unavailable stat ------------------

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("rejected401")]
    public async Task GetSnapshotAsync_NoUsableOAuthAndAdminFails_KeepsTodayBehaviourPlusUnavailableStat(
        string oauthCase)
    {
        // E2 explicitly requires testing the failure leg: "failure → today's behaviour + the
        // 'API usage · Unavailable' stat". "Today's behaviour" is compared against the same OAuth
        // scenario WITHOUT any Admin wiring (the base composition), so no private message text is
        // hardcoded here.
        if (oauthCase is "expired" or "rejected401")
        {
            WriteCredentials(oauthCase == "expired" ? FixedNow.AddHours(-1) : FixedNow.AddHours(1));
        }

        Func<HttpRequestMessage, HttpResponseMessage>? oauthResponder = oauthCase == "rejected401"
            ? _ => Failure(HttpStatusCode.Unauthorized)
            : null;

        // Baseline: plain OAuth-only client (the constructor's existing members only).
        var baseline = CreateBaselineClient(new ClaudeAdminHandler { OAuthResponder = oauthResponder });
        var baselineSnapshot = await baseline.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        // Wired: same scenario, Admin key saved but the Admin call rejected (401).
        var client = CreateAdminClient(new ClaudeAdminHandler
        {
            OAuthResponder = oauthResponder,
            AdminUsageResponder = (_, _) => Failure(HttpStatusCode.Unauthorized),
        });
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

        // E12a: neither signal may be set when the Admin call failed, and the failure stat is present.
        Assert.False(snapshot.AdminApiConnected);
        Assert.False(snapshot.AdminOnly);
        var failed = Assert.Single(snapshot.Stats); // baseline stats are empty here (no history)
        Assert.Equal("API usage", failed.Label);
        Assert.Equal("Unavailable", failed.Value);
        Assert.Equal(KeyRejectedDetail, failed.Detail);
    }

    // ---- E13 (Reviewer1 m6): healthy OAuth with a NON-auth quota failure + Admin ok ---------------

    [Theory]
    [InlineData("quota500")]    // /api/oauth/usage answers 500
    [InlineData("quota429")]    // ... or 429
    [InlineData("quotaTimeout")] // ... or the quota request times out on the client's own deadline
    public async Task GetSnapshotAsync_HealthyOAuthQuotaFailsButAdminOk_AdminOnlyFalseAndErrorKept(
        string quotaCase)
    {
        // E12a/E13: AdminOnly is true ONLY when OAuth is missing, expired or REJECTED (401/403). A
        // healthy, unexpired sign-in whose quota call fails for a transport-ish reason (500/429/
        // timeout) is NOT admin-only — the Admin stats must not repaint a working login as
        // "Claude Code not signed in", and the OAuth error must survive untouched (E1: a failure in
        // one part never removes the other part's output). "Error kept" is compared against the
        // same scenario WITHOUT Admin wiring, so no private message text is hardcoded here.
        WriteCredentials(FixedNow.AddHours(1));
        var timeout = quotaCase == "quotaTimeout" ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5);
        ClaudeAdminHandler NewHandler() => quotaCase switch
        {
            "quota500" => new ClaudeAdminHandler { OAuthResponder = _ => Failure(HttpStatusCode.InternalServerError) },
            "quota429" => new ClaudeAdminHandler { OAuthResponder = _ => Failure(HttpStatusCode.TooManyRequests) },
            _ => new ClaudeAdminHandler { HangOAuth = true },
        };

        var baseline = CreateBaselineClient(NewHandler(), timeout: timeout);
        var baselineSnapshot = await baseline.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        var handler = NewHandler();
        var client = CreateAdminClient(handler, timeout: timeout);
        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        // Sanity: the scenario really produced a quota error on its own (the baseline leg).
        Assert.NotNull(baselineSnapshot.Error);

        // Error kept verbatim from the OAuth leg — and NOT the Admin-only note treatment (E12a).
        Assert.Equal(baselineSnapshot.Error, snapshot.Error);
        Assert.Equal(baselineSnapshot.Unauthorized, snapshot.Unauthorized);
        Assert.False(snapshot.Unauthorized); // a quota failure is not a rejected token
        Assert.Equal(baselineSnapshot.Detection!.State, snapshot.Detection!.State);
        Assert.Equal(DetectionState.Connected, snapshot.Detection!.State); // truthful, unchanged

        // E12a: the Admin call succeeded (flag set) but this is NOT admin-only, so the note and the
        // setup-card exception never apply — only the supplementary stats were appended (E1).
        Assert.True(snapshot.AdminApiConnected);
        Assert.False(snapshot.AdminOnly);
        Assert.DoesNotContain(AdminOnlyNote, AllSnapshotText(snapshot), StringComparison.Ordinal);
        Assert.Equal(
            [CostTodayLabel, CostMonthLabel, TokensMonthLabel],
            snapshot.Stats.Select(s => s.Label).ToArray());
    }

    // ---- E12g: admin-only snapshot carries FetchedAt = clock now and Unauthorized false ----------

    [Fact]
    public async Task GetSnapshotAsync_AdminOnlySnapshot_FetchedAtIsClockNowAndUnauthorizedFalse()
    {
        // E12g: the admin-only snapshot (no OAuth sign-in, Admin call succeeded) is a fresh,
        // usable snapshot: FetchedAt = clock now and Unauthorized = false.
        var client = CreateAdminClient(new ClaudeAdminHandler());

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.AdminOnly);
        Assert.Equal(FixedNow, snapshot.FetchedAt);
        Assert.False(snapshot.Unauthorized);
    }

    // ---- E12f: compact token format boundaries -----------------------------------------------------

    [Theory]
    // Hand-computed from the spec (E12f / handoff (f): never "1000K"; ≥ 1e9 uses "B"; house compact
    // 0.# K/M "like other token stats", so exactly 1000 → "1K" and 15067 → "15.1K").
    [InlineData(1000L, "1K")]                        // exactly 1K
    [InlineData(15067L, "15.1K")]                    // house compact K (matches the C6 success oracle)
    [InlineData(999_949L, "999.9K")]                 // E13 (handoff 11): last value still rendered in K
    [InlineData(999_950L, "1M")]                     // E13: the first value pushed up to M — never "1000K"
    [InlineData(999_999L, "1M")]                     // rounding must not print "1000K" → pushed to M
    [InlineData(1_000_000L, "1M")]                   // exactly 1M
    [InlineData(999_950_000L, "1B")]                 // E13: the first value pushed up to B — never "1000M"
    [InlineData(1_000_000_000L, "1B")]               // ≥ 1e9 uses B
    public async Task GetSnapshotAsync_AdminOk_CompactTokensNeverShow1000K_AndBillionsUseB(
        long monthTokens, string expected)
    {
        // E12f pins the "API tokens this month (UTC)" stat value at the K/M and M/B boundaries.
        WriteCredentials(FixedNow.AddHours(1));
        var usageBody = $$"""{"data":[{"starting_at":"2026-11-15T00:00:00Z","ending_at":"2026-11-16T00:00:00Z","results":[{"model":"m","uncached_input_tokens":{{monthTokens.ToString(CultureInfo.InvariantCulture)}},"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":0}]}],"has_more":false,"next_page":null}""";
        var client = CreateAdminClient(new ClaudeAdminHandler
        {
            AdminUsageResponder = (_, _) => Success(usageBody),
        });

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        var value = StatByName(snapshot, TokensMonthLabel).Value;
        Assert.Equal(expected, value);
        Assert.NotEqual("1000K", value); // E12f, stated as the rule the spec pins
        Assert.NotEqual("1000M", value); // E13: the same rule one unit up (the spec's K/M/B ladder)
    }

    // ---- E6: one client, key read at the start of every refresh ---------------------------------

    [Fact]
    public async Task GetSnapshotAsync_NoSavedAdminKey_MakesNoAdminRequestAndStaysExactlyAsBefore()
    {
        // C6: Admin is called only when a saved key exists. With no key, the snapshot is exactly the
        // OAuth-only result (R-006/R-008/R-011/R-014 unchanged — E10), while the key WAS read (E6).
        WriteCredentials(FixedNow.AddHours(1));
        var reader = new FakeAdminKeyReader { Key = null };
        var handler = new ClaudeAdminHandler();
        var client = CreateAdminClient(handler, keyReader: reader);

        var snapshot = await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Empty(snapshot.Stats);
        Assert.Single(snapshot.Meters);
        Assert.False(snapshot.AdminOnly);
        Assert.False(snapshot.AdminApiConnected);
        Assert.Single(handler.OAuthRequests);
        Assert.Empty(handler.AdminRequests);
        Assert.Equal(1, reader.Reads); // read at the start of the refresh; it just returned nothing
    }

    [Fact]
    public async Task GetSnapshotAsync_ReadsSavedKeyAtStartOfEveryRefresh_NewestKeyGoesOnTheWire()
    {
        // E6: no key cached between refreshes — changing the stored key takes effect on the very next
        // refresh of the SAME client instance (the Connect dialog's existing Save → refresh path, C8).
        WriteCredentials(FixedNow.AddHours(1));
        var reader = new FakeAdminKeyReader { Key = AdminKey };
        var handler = new ClaudeAdminHandler();
        var client = CreateAdminClient(handler, keyReader: reader);

        await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);
        const string replacedKey = "sk-an…-ffff";
        reader.Key = replacedKey;
        await client.GetSnapshotAsync(NoHistoryRequest(), CancellationToken.None);

        Assert.Equal(2, reader.Reads);                       // once per refresh, never cached
        var wireKeys = handler.AdminRequests.Select(r => r.Headers.GetValues("x-api-key").Single()).ToArray();
        Assert.Equal([AdminKey, AdminKey, replacedKey, replacedKey], wireKeys); // (usage+cost) × 2
    }

    // ---- E7/E12d: caller cancellation propagates, never becomes "Unavailable" ---------------------

    [Fact]
    public async Task GetSnapshotAsync_CallerCancelsDuringAdminCall_PropagatesOperationCanceled()
    {
        WriteCredentials(FixedNow.AddHours(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ClaudeAdminHandler { HangAdmin = true, AdminEntered = entered };
        var client = CreateAdminClient(handler);
        using var cts = new CancellationTokenSource();

        var pending = client.GetSnapshotAsync(NoHistoryRequest(), cts.Token);
        await entered.Task;
        cts.Cancel();

        // E7: the caller sees OperationCanceledException — never a snapshot carrying
        // "API usage · Unavailable".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task GetSnapshotAsync_CallerCancelsAfterAdminFailure_PropagatesOperationCanceled()
    {
        // E12d/E13c at the composition level: after any Admin failure, if the caller token is
        // cancelled the refresh throws OperationCanceledException instead of rendering "Unavailable".
        // Deterministic ordering (E13 handoff item 8, no wall-clock window and no race with the test
        // thread): the usage responder cancels the CALLER token itself and then answers 401, so the
        // Admin failure reaches the composition with the caller already gone. (The previous shape
        // cancelled from the test thread after the call; the 401 is processed synchronously, so the
        // refresh had already finished with "API usage · Unavailable" — a test race, not a defect.)
        WriteCredentials(FixedNow.AddHours(1));
        using var cts = new CancellationTokenSource();
        var handler = new ClaudeAdminHandler
        {
            AdminUsageResponder = (_, _) =>
            {
                cts.Cancel();
                return Failure(HttpStatusCode.Unauthorized);
            },
        };
        var client = CreateAdminClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(NoHistoryRequest(), cts.Token));
    }

    // ---- E8: sentinel sweep across every snapshot text --------------------------------------------

    [Fact]
    public void AdminKey_Fixture_ReallyContainsTheSentinelMarker()
    {
        // E13 (handoff item 7): the sweeps below only have teeth if the fake key carries the marker.
        // Assert the prerequisite itself first, so a future edit that "shortens" the key cannot
        // silently re-vacuate the sentinel checks (Inspector B 4).
        Assert.Contains(AdminKeyPrefixSentinel, AdminKey, StringComparison.Ordinal);
        Assert.StartsWith("sk-ant-admin", AdminKey, StringComparison.Ordinal); // shaped like a real Admin key
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminFailureSnapshotTexts_NeverContainSentinelKeyOrBody()
    {
        // Prerequisite first (E13): the sweeping markers only mean something inside a key that has
        // them — see AdminKey_Fixture_ReallyContainsTheSentinelMarker.
        Assert.Contains(AdminKeyPrefixSentinel, AdminKey, StringComparison.Ordinal);

        WriteCredentials(FixedNow.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_directory, "projects"));
        var client = CreateAdminClient(new ClaudeAdminHandler
        {
            AdminUsageResponder = (_, _) => Failure(
                HttpStatusCode.Unauthorized,
                "{\"error\":{\"type\":\"authentication_error\",\"message\":\"bad key " + AdminKey + " " + BodyEchoMarker + "\"}}"),
        });

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
    }

    [Fact]
    public async Task GetSnapshotAsync_AdminSuccessSnapshotTexts_NeverContainKeyOrHeaderNames()
    {
        // E13: the sweep is only meaningful if the fixture key carries the marker (see
        // AdminKey_Fixture_ReallyContainsTheSentinelMarker) — assert it first.
        Assert.Contains(AdminKeyPrefixSentinel, AdminKey, StringComparison.Ordinal);

        // E8 success side: the key travels only in request headers — nothing lands on the snapshot.
        WriteCredentials(FixedNow.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_directory, "projects"));
        var client = CreateAdminClient(new ClaudeAdminHandler());

        var snapshot = await client.GetSnapshotAsync(
            new ProviderSnapshotRequest(includeHistory: true), CancellationToken.None);

        var text = AllSnapshotText(snapshot);
        Assert.DoesNotContain(AdminKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminKeyPrefixSentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain("x-api-key", text, StringComparison.OrdinalIgnoreCase);

        // Sanity that the sweep ran against real content: Equivalent + 3 Admin stats.
        Assert.Equal(4, snapshot.Stats.Count);
    }
}
