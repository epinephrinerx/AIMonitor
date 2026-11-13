using System.Text;

namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// R-020 F4/F5 outcome matrix (Docs/SPEC_R020_R021.md). Each F5 row is one test or one theory row:
/// not on PATH; --version fails (start error, exit != 0, timeout, cap) or has no digit -> version unknown;
/// Major 1 -> auth.json (MayBeOutdated=false, missing file = zero sign-ins); version unknown -> auth.json with
/// MayBeOutdated=true; Major >= 2 -> `opencode auth list` (Source "opencode auth list"), and on any failure
/// (start error, exit != 0, timeout, cap, invalid JSON or shape) -> auth.json with MayBeOutdated=true;
/// auth.json unreadable / > 1 MB / invalid JSON / non-object root -> Error; a missing file is an Error only
/// when F4 is a fallback; the Error text is the fixed sentence; cancellation propagates.
/// Caps: --version stdout 4 KB, auth list stdout 256 KB, auth.json 1 MB (1024 * 1024; "KB"/"MB" read as 1024).
/// </summary>
public sealed class OpenCodeAuthReaderOutcomeTests
{
    private const string GoodAuthJson = """{"anthropic":{"type":"oauth"},"openai":{"type":"api"}}""";
    private static readonly string[] GoodAuthPairs = ["anthropic|OAuth", "openai|API key"];

    // ── Major = 1 ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.19.5")]
    [InlineData("1.19.5\n")]
    [InlineData("v1.0.0")]
    [InlineData("opencode 1.2.3")]
    public async Task MajorOne_ReadsAuthJson_NotOutdated_F5(string versionText)
    {
        var h = new ReaderHarness();
        h.SetVersion(versionText);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal("auth.json", status.Source);
        Assert.False(status.MayBeOutdated);
        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task MajorOne_MissingAuthJson_IsZeroSignIns_NoError_F5()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Empty(status.SignIns);
        Assert.Equal("auth.json", status.Source);
        Assert.False(status.MayBeOutdated);
        Assert.True(string.IsNullOrEmpty(status.Error));
    }

    [Fact]
    public async Task MajorOne_EmptyObjectAuthJson_IsZeroSignIns_NoError_F5()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.SetAuthJson("{}");

        var status = await h.ReadAsync();

        Assert.Empty(status.SignIns);
        Assert.True(string.IsNullOrEmpty(status.Error));
    }

    // ── Version unknown ─────────────────────────────────────────────────────────

    public enum VersionFailure
    {
        StartError,
        NonZeroExit,
        OutputCap,
        NoDigit,
        EmptyOutput,
    }

    private static ReaderHarness VersionFails(VersionFailure failure)
    {
        var h = new ReaderHarness();
        switch (failure)
        {
            case VersionFailure.StartError:
                h.OnVersion = () => throw ReaderHarness.StartFailure();
                break;
            case VersionFailure.NonZeroExit:
                h.OnVersion = () => new FakeOpenCodeProcess("2.2.7\n", exitCode: 1);
                break;
            case VersionFailure.OutputCap:
                // 4 KB cap on --version stdout: one char over (4 * 1024 + 1) even though it starts with a digit.
                h.OnVersion = () => new FakeOpenCodeProcess("2" + new string('x', 4 * 1024));
                break;
            case VersionFailure.NoDigit:
                h.OnVersion = () => new FakeOpenCodeProcess("opencode dev build\n");
                break;
            case VersionFailure.EmptyOutput:
                h.OnVersion = () => new FakeOpenCodeProcess("");
                break;
        }

        return h;
    }

    [Theory]
    [InlineData(VersionFailure.StartError)]
    [InlineData(VersionFailure.NonZeroExit)]
    [InlineData(VersionFailure.OutputCap)]
    [InlineData(VersionFailure.NoDigit)]
    [InlineData(VersionFailure.EmptyOutput)]
    public async Task VersionUnknown_ReadsAuthJson_WithMayBeOutdated_AndNeverRunsAuthList_F5(VersionFailure failure)
    {
        var h = VersionFails(failure);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal("auth.json", status.Source);
        Assert.True(status.MayBeOutdated);
        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
        Assert.DoesNotContain(h.Starts, s => s.ArgumentList.Contains("auth"));
    }

    [Theory]
    [InlineData(VersionFailure.StartError)]
    [InlineData(VersionFailure.NonZeroExit)]
    [InlineData(VersionFailure.OutputCap)]
    [InlineData(VersionFailure.NoDigit)]
    [InlineData(VersionFailure.EmptyOutput)]
    public async Task VersionUnknown_MissingAuthJson_IsAnError_BecauseF4IsAFallback_F5(VersionFailure failure)
    {
        var h = VersionFails(failure);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    [Fact]
    public async Task VersionTimeout_IsVersionUnknown_KillsAndDisposesTheProcess_F2_F5()
    {
        // H15: the `--version` deadline is injected (short here): the hung `--version` is killed at that deadline
        // and the outcome is "version unknown" -> auth.json with MayBeOutdated. Only the internal deadline is a
        // failed command; the caller's token is never involved.
        var h = new ReaderHarness { VersionDeadline = TimeSpan.FromMilliseconds(200) };
        h.OnVersion = () => new FakeOpenCodeProcess("2.2.7\n", hang: true);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.True(h.Processes[0].Killed);
        Assert.True(h.Processes[0].Disposed);
        Assert.Equal("auth.json", status.Source);
        Assert.True(status.MayBeOutdated);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
        Assert.Single(h.Starts);
    }

    [Fact]
    public async Task VersionOutputCap_KillsTheProcess_F2()
    {
        var h = new ReaderHarness();
        h.OnVersion = () => new FakeOpenCodeProcess("1" + new string('x', 4 * 1024), hang: true);

        await h.ReadAsync();

        Assert.True(h.Processes[0].Killed);
    }

    [Fact]
    public async Task VersionStderrIsReadAndDiscarded_NeverChangesTheOutcome_F2()
    {
        var h = new ReaderHarness();
        h.OnVersion = () => new FakeOpenCodeProcess("1.19.5", error: new string('e', 3 * 1024 * 1024));
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.Equal("auth.json", status.Source);
        Assert.False(status.MayBeOutdated);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    // ── Major >= 2 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2.2.7")]
    [InlineData("2.0.0\n")]
    [InlineData("v2.2.7")]
    [InlineData("10.1.0")]
    public async Task MajorTwoOrMore_UsesAuthList_SourceNamed_NotOutdated_F5(string versionText)
    {
        var h = new ReaderHarness();
        h.SetVersion(versionText);
        h.SetListJson("""[{"id":"anthropic","name":"Anthropic","connections":[{"type":"credential","id":"cred_1","method":"oauth"}]}]""");
        h.SetAuthJson("""{"legacy":{"type":"api"}}""");

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal("opencode auth list", status.Source);
        Assert.False(status.MayBeOutdated);
        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(new[] { "Anthropic|OAuth" }, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task MajorTwo_EmptyArray_IsInstalledWithZeroSignIns_NoFallback_F3_F5()
    {
        var h = new ReaderHarness();
        h.UseTwoX("[]");
        h.SetAuthJson(GoodAuthJson); // a legacy file must NOT resurrect sign-ins the service no longer has

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Empty(status.SignIns);
        Assert.Equal("opencode auth list", status.Source);
        Assert.False(status.MayBeOutdated);
        Assert.True(string.IsNullOrEmpty(status.Error));
    }

    [Fact]
    public async Task MajorTwo_EmptyArrayWithWhitespace_IsZeroSignIns_F3()
    {
        var h = new ReaderHarness();
        h.UseTwoX("  [ ]\r\n");

        var status = await h.ReadAsync();

        Assert.Empty(status.SignIns);
        Assert.Equal("opencode auth list", status.Source);
    }

    public enum ListFailure
    {
        StartError,
        NonZeroExit,
        OutputCap,
        InvalidJson,
        EmptyOutput,
        RootNotArray,
        ElementNotObject,
        ElementWithoutNameAndId,
        ConnectionWithoutType,
        ConnectionTypeNotString,
    }

    private static ReaderHarness ListFails(ListFailure failure)
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7\n");
        switch (failure)
        {
            case ListFailure.StartError:
                h.OnList = () => throw ReaderHarness.StartFailure();
                break;
            case ListFailure.NonZeroExit:
                h.OnList = () => new FakeOpenCodeProcess("[]", exitCode: 1);
                break;
            case ListFailure.OutputCap:
                // 256 KB cap: a well-formed array padded with one char over (256 * 1024 + 1).
                h.OnList = () => new FakeOpenCodeProcess("[" + new string(' ', 256 * 1024 - 1) + "]");
                break;
            case ListFailure.InvalidJson:
                h.OnList = () => new FakeOpenCodeProcess("[{\"id\":");
                break;
            case ListFailure.EmptyOutput:
                h.OnList = () => new FakeOpenCodeProcess("");
                break;
            case ListFailure.RootNotArray:
                h.OnList = () => new FakeOpenCodeProcess("""{"id":"anthropic","name":"Anthropic","connections":[]}""");
                break;
            case ListFailure.ElementNotObject:
                h.OnList = () => new FakeOpenCodeProcess("[7]");
                break;
            case ListFailure.ElementWithoutNameAndId:
                h.OnList = () => new FakeOpenCodeProcess("""[{"connections":[{"type":"env","name":"X"}]}]""");
                break;
            case ListFailure.ConnectionWithoutType:
                h.OnList = () => new FakeOpenCodeProcess("""[{"id":"a","name":"A","connections":[{"id":"cred_1","method":"oauth"}]}]""");
                break;
            case ListFailure.ConnectionTypeNotString:
                h.OnList = () => new FakeOpenCodeProcess("""[{"id":"a","name":"A","connections":[{"type":5,"method":"oauth"}]}]""");
                break;
        }

        return h;
    }

    [Theory]
    [InlineData(ListFailure.StartError)]
    [InlineData(ListFailure.NonZeroExit)]
    [InlineData(ListFailure.OutputCap)]
    [InlineData(ListFailure.InvalidJson)]
    [InlineData(ListFailure.EmptyOutput)]
    [InlineData(ListFailure.RootNotArray)]
    [InlineData(ListFailure.ElementNotObject)]
    [InlineData(ListFailure.ElementWithoutNameAndId)]
    [InlineData(ListFailure.ConnectionWithoutType)]
    [InlineData(ListFailure.ConnectionTypeNotString)]
    public async Task MajorTwo_AuthListFails_FallsBackToAuthJson_WithMayBeOutdated_F5(ListFailure failure)
    {
        var h = ListFails(failure);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal("auth.json", status.Source);
        Assert.True(status.MayBeOutdated);
        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    [Theory]
    [InlineData(ListFailure.StartError)]
    [InlineData(ListFailure.NonZeroExit)]
    [InlineData(ListFailure.OutputCap)]
    [InlineData(ListFailure.InvalidJson)]
    [InlineData(ListFailure.RootNotArray)]
    [InlineData(ListFailure.ElementNotObject)]
    [InlineData(ListFailure.ElementWithoutNameAndId)]
    [InlineData(ListFailure.ConnectionWithoutType)]
    public async Task MajorTwo_AuthListFails_AndAuthJsonMissing_IsAnError_F5(ListFailure failure)
    {
        var h = ListFails(failure);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    [Fact]
    public async Task AuthListTimeout_FallsBackToAuthJson_KillsAndDisposesTheProcess_F2_F5()
    {
        // H15: the `auth list` deadline is injected (short here).
        var h = new ReaderHarness { AuthListDeadline = TimeSpan.FromMilliseconds(200) };
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("[]", hang: true);
        h.SetAuthJson(GoodAuthJson);

        var status = await h.ReadAsync();

        Assert.True(h.Processes[1].Killed);
        Assert.True(h.Processes[1].Disposed);
        Assert.Equal("auth.json", status.Source);
        Assert.True(status.MayBeOutdated);
        Assert.Equal(GoodAuthPairs, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task AuthListTimeout_WithoutAuthJson_IsAnError_F5()
    {
        var h = new ReaderHarness { AuthListDeadline = TimeSpan.FromMilliseconds(200) };
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("[]", hang: true);

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    [Fact]
    public async Task AuthListOutputCap_KillsTheProcess_F2()
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("[" + new string(' ', 256 * 1024) + "]", hang: true);

        await h.ReadAsync();

        Assert.True(h.Processes[1].Killed);
    }

    [Fact]
    public async Task AuthListUnderTheCap_StillParses_F2()
    {
        // A list well above the 4 KB --version cap but under the 256 KB auth list cap must be accepted.
        var padding = new string(' ', 100 * 1024);
        var h = new ReaderHarness();
        h.UseTwoX("[" + padding + """{"id":"anthropic","name":"Anthropic","connections":[{"type":"credential","method":"key"}]}""" + "]");

        var status = await h.ReadAsync();

        Assert.Equal("opencode auth list", status.Source);
        Assert.Equal(new[] { "Anthropic|API key" }, ReaderHarness.Pairs(status));
    }

    // ── auth.json failures (F4/F5) ──────────────────────────────────────────────

    public enum AuthFileFailure
    {
        IoError,
        PermissionError,
        OverOneMegabyte,
        InvalidJson,
        RootArray,
        RootString,
        EmptyFile,
    }

    private static void BreakAuthJson(ReaderHarness h, AuthFileFailure failure)
    {
        switch (failure)
        {
            case AuthFileFailure.IoError:
                h.SetAuthJson("{}");
                h.FailOpen(ReaderHarness.DefaultAuthJson, new IOException("SENTINEL io failure at C:\\secret\\path"));
                break;
            case AuthFileFailure.PermissionError:
                h.SetAuthJson("{}");
                h.FailOpen(ReaderHarness.DefaultAuthJson, new UnauthorizedAccessException("SENTINEL denied"));
                break;
            case AuthFileFailure.OverOneMegabyte:
                // 1 MB + 1 byte of otherwise valid JSON (whitespace padding).
                h.AddFile(ReaderHarness.DefaultAuthJson, "{" + new string(' ', 1024 * 1024 - 1) + "}");
                break;
            case AuthFileFailure.InvalidJson:
                h.SetAuthJson("""{"anthropic":{"type":"oauth",""");
                break;
            case AuthFileFailure.RootArray:
                h.SetAuthJson("""[{"type":"oauth"}]""");
                break;
            case AuthFileFailure.RootString:
                h.SetAuthJson("\"anthropic\"");
                break;
            case AuthFileFailure.EmptyFile:
                h.SetAuthJson("");
                break;
        }
    }

    [Theory]
    [InlineData(AuthFileFailure.IoError)]
    [InlineData(AuthFileFailure.PermissionError)]
    [InlineData(AuthFileFailure.OverOneMegabyte)]
    [InlineData(AuthFileFailure.InvalidJson)]
    [InlineData(AuthFileFailure.RootArray)]
    [InlineData(AuthFileFailure.RootString)]
    public async Task MajorOne_UnusableAuthJson_IsAnError_WithTheFixedText_F5(AuthFileFailure failure)
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        BreakAuthJson(h, failure);

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
        ReaderHarness.AssertNoLeak(status, "SENTINEL", "secret", @"C:\", "auth.json");
    }

    [Theory]
    [InlineData(AuthFileFailure.IoError)]
    [InlineData(AuthFileFailure.PermissionError)]
    [InlineData(AuthFileFailure.OverOneMegabyte)]
    [InlineData(AuthFileFailure.InvalidJson)]
    [InlineData(AuthFileFailure.RootArray)]
    public async Task VersionUnknown_UnusableAuthJson_IsAnError_F5(AuthFileFailure failure)
    {
        var h = new ReaderHarness();
        h.OnVersion = () => throw ReaderHarness.StartFailure();
        BreakAuthJson(h, failure);

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    [Theory]
    [InlineData(AuthFileFailure.IoError)]
    [InlineData(AuthFileFailure.OverOneMegabyte)]
    [InlineData(AuthFileFailure.InvalidJson)]
    public async Task MajorTwo_AuthListFailed_AndAuthJsonUnusable_IsAnError_F5(AuthFileFailure failure)
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("not json");
        BreakAuthJson(h, failure);

        var status = await h.ReadAsync();

        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
        Assert.Empty(status.SignIns);
    }

    [Fact]
    public async Task AuthJsonOfExactlyOneMegabyte_IsAccepted_F4()
    {
        // "Read at most 1 MB": a file of exactly 1024 * 1024 bytes is still readable.
        var json = """{"anthropic":{"type":"oauth"}}""";
        var content = json + new string(' ', 1024 * 1024 - json.Length);
        Assert.Equal(1024 * 1024, Encoding.UTF8.GetByteCount(content));
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.AddFile(ReaderHarness.DefaultAuthJson, content);

        var status = await h.ReadAsync();

        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(new[] { "anthropic|OAuth" }, ReaderHarness.Pairs(status));
    }

    [Fact]
    public async Task EmptyAuthJsonFile_IsNeverAThrow_F5()
    {
        // An empty file is not valid JSON. The spec rule is "invalid JSON -> Error"; the test only pins that
        // the reader returns a status (never throws) and never invents a sign-in.
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        BreakAuthJson(h, AuthFileFailure.EmptyFile);

        var status = await h.ReadAsync();

        Assert.Empty(status.SignIns);
        Assert.Equal(ReaderHarness.ExpectedErrorText, status.Error);
    }

    // ── auth.json location (F4) ─────────────────────────────────────────────────

    [Fact]
    public async Task AuthJsonPath_UsesRootedXdgDataHome_F4()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.Env["XDG_DATA_HOME"] = @"D:\xdg\data";
        h.AddFile(@"D:\xdg\data\opencode\auth.json", """{"fromxdg":{"type":"oauth"}}""");
        h.SetAuthJson("""{"fromprofile":{"type":"oauth"}}""");

        var status = await h.ReadAsync();

        Assert.Equal(new[] { "fromxdg|OAuth" }, ReaderHarness.Pairs(status));
        Assert.Equal(new[] { @"D:\xdg\data\opencode\auth.json" }, h.AccessedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\xdg")]
    public async Task AuthJsonPath_FallsBackToUserProfile_WhenXdgDataHomeIsUnsetOrNotRooted_F4(string? xdg)
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        if (xdg is not null)
        {
            h.Env["XDG_DATA_HOME"] = xdg;
            h.AddFile(Path.Combine(xdg, "opencode", "auth.json"), """{"fromxdg":{"type":"oauth"}}""");
        }

        h.SetAuthJson("""{"fromprofile":{"type":"api"}}""");

        var status = await h.ReadAsync();

        Assert.Equal(new[] { "fromprofile|API key" }, ReaderHarness.Pairs(status));
        Assert.Equal(
            new[] { @"C:\Users\tester\.local\share\opencode\auth.json" },
            h.AccessedPaths,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenCodeAuthContentEnvironmentVariable_IsIgnored_F4()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.Env["OPENCODE_AUTH_CONTENT"] = """{"fromenv":{"type":"oauth"}}""";
        h.SetAuthJson("""{"fromfile":{"type":"api"}}""");

        var status = await h.ReadAsync();

        Assert.Equal(new[] { "fromfile|API key" }, ReaderHarness.Pairs(status));
        Assert.DoesNotContain("OPENCODE_AUTH_CONTENT", h.EnvironmentReads, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthJsonIsOpenedExactlyOnce_F4()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.SetAuthJson(GoodAuthJson);

        await h.ReadAsync();

        Assert.Single(h.OpenedFiles);
    }

    // ── Cancellation (F5) ───────────────────────────────────────────────────────

    [Fact]
    public async Task CancelledBeforeStart_Propagates_F5()
    {
        var h = new ReaderHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ReadAsync(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CancelDuringVersion_PropagatesAndKillsTheProcess_F5_F2()
    {
        var h = new ReaderHarness();
        h.OnVersion = () => new FakeOpenCodeProcess("1.19.5", hang: true);
        using var cts = new CancellationTokenSource();

        var read = h.ReadAsync(cancellationToken: cts.Token);
        await WaitForProcessWait(h, 0);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(h.Processes[0].Killed);
        Assert.Single(h.Starts); // no auth.json / auth list afterwards
    }

    [Fact]
    public async Task CancelDuringAuthList_PropagatesAndKillsTheProcess_NoFallbackStatus_F5_F2()
    {
        var h = new ReaderHarness();
        h.SetVersion("2.2.7");
        h.OnList = () => new FakeOpenCodeProcess("[]", hang: true);
        h.SetAuthJson(GoodAuthJson);
        using var cts = new CancellationTokenSource();

        var read = h.ReadAsync(cancellationToken: cts.Token);
        await WaitForProcessWait(h, 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(h.Processes[1].Killed);
    }

    private static Task WaitForProcessWait(ReaderHarness h, int index) => h.WaitForProcessAsync(index);
}
