using System.Text;
using AIMonitor.Application.OpenCode;
using AIMonitor.Infrastructure.OpenCode;

namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// R-020 fix round 1 (Docs/SPEC_R020_R021.md, "Fix round 1"): the reader hardening rows H1, H3, H4, H5, H6, H10
/// and H15. (H2 is in <see cref="OpenCodeAuthReaderSecretTests"/>; H7, H8 and H9 are in
/// <see cref="OpenCodeAuthReaderMappingTests"/>.) Every wait on the reader is bounded by <see cref="Bound"/> so a
/// reader that waits for the (30 s, never reached) harness deadline fails fast instead of hanging the run.
/// </summary>
public sealed class OpenCodeAuthReaderFixRound1Tests
{
    private const string GoodAuthJson = """{"anthropic":{"type":"oauth"},"openai":{"type":"api"}}""";
    private static readonly string[] GoodAuthPairs = ["anthropic|OAuth", "openai|API key"];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(200);

    /// <summary>J6: a deadline a step that MUST succeed runs under: far above any scheduling stall on a loaded CI
    /// machine (the 200 ms one is only for the step that is meant to time out).</summary>
    private static readonly TimeSpan MustSucceedDeadline = TimeSpan.FromSeconds(2);

    /// <summary>Longer than <see cref="MustSucceedDeadline"/> by a margin, so the other command proves independence.</summary>
    private static readonly TimeSpan SlowerThanTheMustSucceedDeadline = TimeSpan.FromMilliseconds(2500);
    private const int OneMegabytePlusOne = (1024 * 1024) + 1;

    // ── H1: caller cancellation propagates ──────────────────────────────────────

    [Fact]
    public async Task CallerCancelWhileStdoutIsBlocked_PropagatesAndKills_NeverBecomesAFailedCommand_H1()
    {
        var h = new ReaderHarness();
        h.OnVersion = () => new FakeOpenCodeProcess(new StallingReader(honorCancellation: true), new StallingReader(honorCancellation: true), hang: true);
        h.SetAuthJson(GoodAuthJson); // a swallowed cancellation would "succeed" through this fallback
        using var cts = new CancellationTokenSource();

        var read = h.ReadAsync(cts.Token);
        await h.WaitForProcessAsync(0);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
        Assert.True(h.Processes[0].Killed);
        Assert.Single(h.Starts); // no `auth list`, no auth.json afterwards
        Assert.Empty(h.OpenedFiles);
    }

    [Fact]
    public async Task CallerCancelWhileAuthListStdoutIsBlocked_Propagates_NoFallbackToAuthJson_H1()
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess(new StallingReader(honorCancellation: true), new StringReader(""), hang: true);
        h.SetAuthJson(GoodAuthJson);
        using var cts = new CancellationTokenSource();

        var read = h.ReadAsync(cts.Token);
        await h.WaitForProcessAsync(1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
        Assert.True(h.Processes[1].Killed);
        Assert.Empty(h.OpenedFiles);
    }

    [Fact]
    public async Task CallerCancelDuringTheAuthJsonRead_Propagates_NoErrorStatus_H1()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.ReadOverride = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };
        using var cts = new CancellationTokenSource();

        var read = h.ReadAsync(cts.Token);
        await entered.Task.WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
    }

    [Fact]
    public async Task InternalDeadline_IsAFailedCommand_NotACancellation_H1()
    {
        // Only the internal deadline is treated as a failed command: no exception reaches the caller.
        var h = new ReaderHarness { VersionDeadline = ShortDeadline };
        h.OnVersion = () => new FakeOpenCodeProcess(new StallingReader(honorCancellation: true), new StringReader(""), hang: true);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync().WaitAsync(Bound);

        Assert.Equal("auth.json", status.Source);
        Assert.True(status.MayBeOutdated);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
        Assert.True(h.Processes[0].Killed);
    }

    // ── H3: the output cap kills at once; every wait is bounded ─────────────────

    public enum StderrBehavior
    {
        EndsFirst,
        NeverEnds,
        FloodsToo,
    }

    private static TextReader Stderr(StderrBehavior behavior) => behavior switch
    {
        StderrBehavior.EndsFirst => new StringReader(""),
        StderrBehavior.NeverEnds => new StallingReader(honorCancellation: true),
        _ => new FloodingReader(),
    };

    [Theory]
    [InlineData("version", StderrBehavior.EndsFirst)]
    [InlineData("version", StderrBehavior.NeverEnds)]
    [InlineData("version", StderrBehavior.FloodsToo)]
    [InlineData("list", StderrBehavior.EndsFirst)]
    [InlineData("list", StderrBehavior.NeverEnds)]
    [InlineData("list", StderrBehavior.FloodsToo)]
    public async Task StdoutThatKeepsProducingPastTheCap_KillsTheTreeAtOnce_WhateverStderrAndExitDo_H3(string command, StderrBehavior stderr)
    {
        // Stdout never ends and keeps producing asynchronously; exit never completes on its own. The harness
        // deadline is 30 s: a reader that waits for stderr or for exit first only returns at that deadline, which
        // is later than Bound, so the WaitAsync below fails it. "stderr ends first" is Reviewer2 #1's ordering.
        var h = new ReaderHarness();
        var index = command == "version" ? 0 : 1;
        if (command == "version")
        {
            h.OnVersion = () => new FakeOpenCodeProcess(new FloodingReader(), Stderr(stderr), hang: true);
        }
        else
        {
            h.SetVersion("2.2.7");
            h.OnList = () => new FakeOpenCodeProcess(new FloodingReader(), Stderr(stderr), hang: true);
        }

        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync().WaitAsync(Bound);

        Assert.True(h.Processes[index].Killed);
        Assert.True(h.Processes[index].Disposed);
        Assert.Equal("auth.json", status.Source); // the capped command failed -> F4 fallback
        Assert.True(status.MayBeOutdated);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamThatIgnoresCancellation_IsStillBoundedByTheDeadline_AndTheProcessIsKilled_H3(bool stdoutStalls)
    {
        // The process exits at once, but one pipe never ends (and ignores its token) until the process is killed.
        // No wait in the reader may be unbounded: the deadline abandons the wait, kills, and fails the command.
        var h = new ReaderHarness { VersionDeadline = ShortDeadline };
        h.OnVersion = () => stdoutStalls
            ? new FakeOpenCodeProcess(new StallingReader(honorCancellation: false), new StringReader(""))
            : new FakeOpenCodeProcess(new StringReader("1.19.5"), new StallingReader(honorCancellation: false));
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync().WaitAsync(Bound);

        Assert.True(h.Processes[0].Killed);
        Assert.True(h.Processes[0].Disposed);
        if (stdoutStalls)
        {
            // No version text can ever arrive: the command failed at the deadline -> version unknown -> F4.
            Assert.Equal("auth.json", status.Source);
            Assert.True(status.MayBeOutdated);
            Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
        }

        // (When only stderr stalls, the spec leaves open whether the command then counts as failed; the test
        // pins only that the read ends and the process is killed.)
    }

    [Fact]
    public async Task ExitThatNeverCompletes_WithBothStreamsEnded_IsBoundedByTheDeadline_H3()
    {
        var h = new ReaderHarness { AuthListDeadline = ShortDeadline };
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("[]", hang: true);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync().WaitAsync(Bound);

        Assert.True(h.Processes[1].Killed);
        Assert.Equal("auth.json", status.Source);
        Assert.True(status.MayBeOutdated);
    }

    // ── H4: the auth.json read itself is bounded ────────────────────────────────

    [Fact]
    public async Task AuthJsonRead_AsksForAtMostOneMegabytePlusOneByte_H4()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
        Assert.Equal(new[] { OneMegabytePlusOne }, h.ReadLimits);
    }

    [Fact]
    public async Task AuthJsonMuchLargerThanTheLimit_IsAnError_AndTheReadWasNeverAskedForMoreThanTheLimit_H4()
    {
        // The seam is the only way to read the file now, so a file that is bigger than any earlier "length" (or that
        // grew since) can never make the reader hold more than the limit: the fake truncates to what it is asked.
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.AddFile(ReaderHarness.DefaultAuthJson, "{" + new string(' ', 8 * 1024 * 1024) + "}");

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
        Assert.All(h.ReadLimits, limit => Assert.Equal(OneMegabytePlusOne, limit));
    }

    [Fact]
    public async Task ExactlyOneMegabytePlusOneByte_IsAnError_ButExactlyOneMegabyteIsNot_H4()
    {
        var json = """{"anthropic":{"type":"oauth"}}""";
        var over = new ReaderHarness();
        over.SetVersion("1.19.5");
        over.AddFile(ReaderHarness.DefaultAuthJson, json + new string(' ', OneMegabytePlusOne - json.Length));
        var exact = new ReaderHarness();
        exact.SetVersion("1.19.5");
        exact.AddFile(ReaderHarness.DefaultAuthJson, json + new string(' ', (1024 * 1024) - json.Length));

        var overStatus = await over.ReadAsync();
        var exactStatus = await exact.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, overStatus.Error);
        Assert.True(string.IsNullOrEmpty(exactStatus.Error));
        Assert.Equal(new[] { "anthropic|OAuth" }, ReaderHarness.Pairs(exactStatus));
    }

    [Fact]
    public async Task ASeamThatReturnsMoreThanTheLimit_IsStillAnError_H4()
    {
        // "The reader ... treats more than 1 MB as Error": defence in depth when a seam over-delivers.
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.ReadOverride = (_, _, _) => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes("{" + new string(' ', (1024 * 1024) + 100) + "}"));

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    [Fact]
    public async Task OversizeAuthJson_AsFallback_IsAlsoAnErrorAndAlsoBounded_H4()
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("not json");
        h.AddFile(ReaderHarness.DefaultAuthJson, "{" + new string(' ', 4 * 1024 * 1024) + "}");

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.All(h.ReadLimits, limit => Assert.Equal(OneMegabytePlusOne, limit));
    }

    // ── H5: a leading UTF-8 BOM is skipped ──────────────────────────────────────

    private static byte[] WithBom(string json) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)];

    [Fact]
    public async Task LeadingUtf8Bom_IsSkipped_MajorOne_H5()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.AddFile(ReaderHarness.DefaultAuthJson, WithBom(GoodAuthJson));

        var status = await h.ReadAsync();

        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal("auth.json", status.Source);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task LeadingUtf8Bom_IsSkipped_WhenAuthJsonIsTheFallback_H5()
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("not json");
        h.AddFile(ReaderHarness.DefaultAuthJson, WithBom(GoodAuthJson));

        var status = await h.ReadAsync();

        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.True(status.MayBeOutdated);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task BomWithEmptyObject_IsZeroSignIns_NoError_H5()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.AddFile(ReaderHarness.DefaultAuthJson, WithBom("{}"));

        var status = await h.ReadAsync();

        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Empty(status.SignIns);
    }

    [Fact]
    public async Task BomWithoutAnyJson_IsStillInvalidJson_AnError_H5()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.AddFile(ReaderHarness.DefaultAuthJson, new byte[] { 0xEF, 0xBB, 0xBF });

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    // ── H6: XDG_DATA_HOME and USERPROFILE must be rooted, non-drive-relative ────

    [Theory]
    [InlineData("C:foo")]
    [InlineData("C:")]
    [InlineData(@"D:data\xdg")]
    public async Task DriveRelativeXdgDataHome_IsIgnored_UserProfileIsUsed_H6(string xdg)
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.Env["XDG_DATA_HOME"] = xdg;
        h.AddFile(Path.Combine(xdg, "opencode", "auth.json"), """{"fromxdg":{"type":"oauth"}}""");
        h.SetAuthJson("""{"fromprofile":{"type":"api"}}""");

        var status = await h.ReadAsync();

        Assert.Equal(new[] { "fromprofile|API key" }, ReaderHarness.Pairs(status));
        Assert.Equal(new[] { ReaderHarness.DefaultAuthJson }, h.AccessedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.19.5", null)]
    [InlineData("1.19.5", "")]
    [InlineData("1.19.5", "   ")]
    [InlineData("1.19.5", @"relative\home")]
    [InlineData("1.19.5", "C:home")]
    [InlineData("1.19.5", "C:")]
    [InlineData("unknown", null)]
    [InlineData("unknown", "")]
    [InlineData("unknown", @"relative\home")]
    [InlineData("unknown", "C:home")]
    [InlineData("2.2.7-failed", null)]
    [InlineData("2.2.7-failed", @"relative\home")]
    [InlineData("2.2.7-failed", "C:home")]
    public async Task UnusableUserProfile_WhenTheFallbackNeedsIt_IsAnError_AndNothingIsRead_H6(string scenario, string? userProfile)
    {
        var h = new ReaderHarness();
        switch (scenario)
        {
            case "1.19.5":
                h.SetVersion("1.19.5");
                break;
            case "unknown":
                h.OnVersion = () => throw ReaderHarness.StartFailure();
                break;
            default:
                h.SetVersion("2.2.7");
                h.OnList = () => new FakeOpenCodeProcess("not json");
                break;
        }

        if (userProfile is null)
        {
            h.Env.Remove(OpenCodeAuthReader.ProfileVariable);
        }
        else
        {
            h.Env[OpenCodeAuthReader.ProfileVariable] = userProfile;
        }

        // Files that a working-directory-relative path would find must stay unread.
        h.AddFile(@".local\share\opencode\auth.json", GoodAuthJson);
        h.AddFile(Path.Combine(userProfile ?? "x", ".local", "share", "opencode", "auth.json"), GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
        Assert.Empty(h.OpenedFiles);
    }

    [Fact]
    public async Task RootedXdgDataHome_NeedsNoUserProfile_H6()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.Env.Remove(OpenCodeAuthReader.ProfileVariable);
        h.Env["XDG_DATA_HOME"] = ReaderHarness.XdgRoot;
        h.AddFile(Path.Combine(ReaderHarness.XdgRoot, "opencode", "auth.json"), """{"fromxdg":{"type":"oauth"}}""");

        var status = await h.ReadAsync();

        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(new[] { "fromxdg|OAuth" }, ReaderHarness.Pairs(status));
    }

    // ── H10: major 0 is "version unknown" ───────────────────────────────────────

    [Theory]
    [InlineData("0.9.1")]
    [InlineData("v0.0.1\n")]
    [InlineData("opencode 0.15.3")]
    public async Task MajorZero_IsHandledLikeVersionUnknown_AuthJsonWithMayBeOutdated_NoAuthList_H10(string versionText)
    {
        var h = new ReaderHarness();
        h.SetVersion(versionText);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal("auth.json", status.Source);
        Assert.True(status.MayBeOutdated);
        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
        Assert.Single(h.Starts);
        Assert.DoesNotContain(h.Starts, s => s.ArgumentList.Contains("auth"));
    }

    [Fact]
    public async Task MajorZero_WithoutAuthJson_IsAnError_BecauseF4IsAFallback_H10()
    {
        var h = new ReaderHarness();
        h.SetVersion("0.9.1");

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    // ── H15: injectable deadlines, defaults 10 s / 20 s ─────────────────────────

    [Fact]
    public void PublicConstructor_UsesTenSecondsForVersionAndTwentyForAuthList_H15()
    {
        var reader = new OpenCodeAuthReader();

        Assert.Equal(TimeSpan.FromSeconds(10), reader.VersionDeadline);
        Assert.Equal(TimeSpan.FromSeconds(20), reader.AuthListDeadline);
    }

    [Fact]
    public async Task InjectedDeadlines_AreTheOnesUsed_AndAreIndependent_H15()
    {
        // The `--version` deadline is short, the `auth list` deadline is long: an `auth list` that takes longer than
        // the version deadline must still succeed (and the other way round in the next test).
        var h = new ReaderHarness { VersionDeadline = MustSucceedDeadline, AuthListDeadline = TimeSpan.FromSeconds(30) };
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess(
            new DelayedReader(SlowerThanTheMustSucceedDeadline, """[{"id":"a","name":"Anthropic","connections":[{"type":"credential","method":"oauth"}]}]"""),
            new StringReader(""));

        var status = await h.ReadAsync().WaitAsync(Bound);

        Assert.Equal("opencode auth list", status.Source);
        Assert.Equal(new[] { "Anthropic|OAuth" }, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task InjectedVersionDeadline_IsLongerThanTheAuthListOne_Independently_H15()
    {
        var h = new ReaderHarness { VersionDeadline = TimeSpan.FromSeconds(30), AuthListDeadline = MustSucceedDeadline };
        h.OnVersion = () => new FakeOpenCodeProcess(new DelayedReader(SlowerThanTheMustSucceedDeadline, "2.2.7"), new StringReader(""));
        h.OnList = () => new FakeOpenCodeProcess("[]");

        var status = await h.ReadAsync().WaitAsync(Bound);

        Assert.Equal("opencode auth list", status.Source); // the version took longer than the list deadline and still parsed as 2.x
        Assert.Empty(status.SignIns);
    }

    /// <summary>Produces its text only after a delay (an honest slow command), then ends.</summary>
    private sealed class DelayedReader(TimeSpan delay, string text) : ScriptedReader
    {
        private bool _waited;
        private int _position;

        protected override async Task<int> ReadCoreAsync(Memory<char> buffer, CancellationToken cancellationToken)
        {
            if (!_waited)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                _waited = true;
            }

            var n = Math.Min(buffer.Length, text.Length - _position);
            text.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return n;
        }
    }
}
