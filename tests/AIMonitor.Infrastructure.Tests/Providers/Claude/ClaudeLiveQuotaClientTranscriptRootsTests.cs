using AIMonitor.Application.Providers;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Covers SPEC R-014 "P2b detailed" §P2b.6 - <see cref="ClaudeLiveQuotaClient"/> builds its transcript
/// store from <c>ResolveAll(claudeConfigDir, xdgConfigHome, userProfile)</c> instead of the single
/// <c>Resolve(...)</c> root. Only the part of that rule that is fully determined by the spec is tested
/// here: a comma-separated <c>CLAUDE_CONFIG_DIR</c> has to reach the store as several roots, and rule 1's
/// "Defaults are not added" has to hold through the client. B21 settled the open question this first
/// raised about the <c>XDG_CONFIG_HOME</c> half: the client ctors take a trailing optional
/// <c>xdgConfigHome</c> parameter, so rule 2's xdg default is fed through it below.
/// </summary>
/// <remarks>
/// No credentials file is written, which the existing
/// <c>ClaudeLiveQuotaClientHistoryTests.GetSnapshotAsync_MissingCredentials_HistoryIsStillReadAndReported</c>
/// establishes still reads and reports local transcripts (PAR-004: the quota failure and the history
/// outcome are independent). Temp directories only, synthetic content, no real credential or network call.
/// </remarks>
[Trait("Category", "Contract")]
public sealed class ClaudeLiveQuotaClientTranscriptRootsTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-client-roots-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _profile;
    private readonly string _configDirOne;
    private readonly string _configDirTwo;
    private readonly string _xdg;

    public ClaudeLiveQuotaClientTranscriptRootsTests()
    {
        _profile = Path.Combine(_base, "profile");
        _configDirOne = Path.Combine(_base, "config-one");
        _configDirTwo = Path.Combine(_base, "config-two");
        _xdg = Path.Combine(_base, "xdg");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(_configDirOne);
        Directory.CreateDirectory(_configDirTwo);
        Directory.CreateDirectory(_xdg);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    /// <summary>Writes one assistant record (1,000 input tokens, claude-opus-5) into the
    /// <c>projects</c> directory under <paramref name="configRoot"/>. No <c>cwd</c> is written, so the
    /// record's project label is the encoded project folder itself - which is what identifies the root it
    /// was read from.</summary>
    private static void WriteTranscript(string configRoot, string encodedProjectFolder, string messageId)
    {
        var project = Path.Combine(configRoot, "projects", encodedProjectFolder);
        Directory.CreateDirectory(project);
        var line = TranscriptRecordBuilder
            .WithTokens(1_000, 0, id: messageId, model: "claude-opus-5")
            .SessionId("session-" + messageId)
            .Cwd(null)
            .Line();
        File.WriteAllText(Path.Combine(project, "session.jsonl"), line + "\n");
    }

    private ClaudeLiveQuotaClient CreateClient(string? claudeConfigDir, string? xdgConfigHome = null)
    {
        // B21: the trailing optional xdgConfigHome ctor parameter lets a test feed rule 2's
        // XDG_CONFIG_HOME input without touching the real environment.
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)));
        return new ClaudeLiveQuotaClient(
            new HttpClient(handler),
            claudeConfigDir,
            _profile,
            new FakeClock(FixedNow),
            timeout: TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc,
            xdgConfigHome: xdgConfigHome);
    }

    private static ProviderSnapshotRequest HistoryRequest() =>
        new(historyDays: 14, metric: UsageMetric.TotalTokens, includeHistory: true);

    [Fact]
    public async Task GetSnapshotAsync_CommaSeparatedConfigDir_ReadsTranscriptsFromEveryEntry()
    {
        // P2b.1 rule 1 through P2b.6: each CLAUDE_CONFIG_DIR entry becomes its own projects root, and the
        // data from all roots is combined. Before P2b the whole comma string was one path that never
        // existed, so neither transcript would be read at all.
        WriteTranscript(_configDirOne, "C--work-one", "msg-one");
        WriteTranscript(_configDirTwo, "C--work-two", "msg-two");

        var snapshot = await CreateClient(_configDirOne + "," + _configDirTwo)
            .GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.Null(snapshot.HistoryError);
        Assert.NotNull(snapshot.History);

        var projects = snapshot.History!.ByProject.Select(entry => entry.Label).ToArray();
        Assert.Contains("one", projects);
        Assert.Contains("two", projects);
    }

    [Fact]
    public async Task GetSnapshotAsync_ConfigDirSet_DoesNotAlsoReadTheProfileDefaultRoot()
    {
        // P2b.1 rule 1: "Defaults are not added" - so a transcript that only exists under
        // <profile>\.claude\projects must not reach the snapshot while CLAUDE_CONFIG_DIR is set.
        WriteTranscript(_configDirOne, "C--work-one", "msg-one");
        WriteTranscript(Path.Combine(_profile, ".claude"), "C--work-default", "msg-default");

        var snapshot = await CreateClient(_configDirOne)
            .GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.Null(snapshot.HistoryError);
        Assert.NotNull(snapshot.History);

        var projects = snapshot.History!.ByProject.Select(entry => entry.Label).ToArray();
        Assert.Equal(["one"], projects);
    }

    [Fact]
    public async Task GetSnapshotConfigDir_WhitespaceValue_FallsBackToTheProfileDefaultRoot()
    {
        // A11 through P2b.6: a whitespace-only CLAUDE_CONFIG_DIR is treated as unset, so the profile
        // default root is read - and nothing reports a missing directory.
        WriteTranscript(Path.Combine(_profile, ".claude"), "C--work-default", "msg-default");

        var snapshot = await CreateClient("   ").GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.Null(snapshot.HistoryError);
        Assert.Equal(["default"], snapshot.History!.ByProject.Select(entry => entry.Label));
    }

    [Fact]
    public async Task GetSnapshotAsync_XdgConfigHomeSupplied_ReadsTheXdgDefaultRoot()
    {
        // P2b.1 rule 2 through P2b.6 and B21: with CLAUDE_CONFIG_DIR unset, an XDG_CONFIG_HOME handed to
        // the constructor moves the first default root to <xdg>\claude\projects - a transcript written
        // there reaches the snapshot. (In production App.xaml.cs passes the real environment variable;
        // <profile>\.claude\projects stays as the second default, but no transcript is written under it.)
        WriteTranscript(Path.Combine(_xdg, "claude"), "C--work-xdg", "msg-xdg");

        var snapshot = await CreateClient(null, _xdg)
            .GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.Null(snapshot.HistoryError);
        Assert.Equal(["xdg"], snapshot.History!.ByProject.Select(entry => entry.Label));
    }

    [Fact]
    public async Task GetSnapshotAsync_ConfigDirSet_XdgDefaultIsNotAdded()
    {
        // Rule 1's "Defaults are not added" covers the xdg default too: with CLAUDE_CONFIG_DIR set, the
        // xdgConfigHome parameter changes nothing and only the entry's root is scanned.
        WriteTranscript(_configDirOne, "C--work-one", "msg-one");
        WriteTranscript(Path.Combine(_xdg, "claude"), "C--work-xdg", "msg-xdg");

        var snapshot = await CreateClient(_configDirOne, _xdg)
            .GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.Null(snapshot.HistoryError);
        Assert.Equal(["one"], snapshot.History!.ByProject.Select(entry => entry.Label));
    }
}
