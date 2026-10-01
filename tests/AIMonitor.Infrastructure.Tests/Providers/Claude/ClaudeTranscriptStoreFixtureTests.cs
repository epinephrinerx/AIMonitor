using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Exercises <see cref="ClaudeTranscriptStore.RefreshAsync"/> against golden, sanitized JSONL fixtures
/// under <c>tests/Fixtures/Claude/Transcripts/</c> rather than inline-generated content, for the same
/// reason the Claude quota payload contract tests use golden fixtures: a fixture is reviewable on its
/// own and exercises the ingest pipeline end to end rather than only the one path a hand-written
/// inline test happens to construct. No fixture here contains a real credential, token, or path.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreFixtureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-transcript-fixture-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _project;

    public ClaudeTranscriptStoreFixtureTests()
    {
        _project = Path.Combine(_root, "C--home-dev-project");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Claude", "Transcripts", fileName);

    private void InstallFixture(string fileName) =>
        File.Copy(FixturePath(fileName), Path.Combine(_project, "session.jsonl"));

    private ClaudeTranscriptStore CreateStore(DateTimeOffset now) => new(_root, new FakeClock(now), TimeZoneInfo.Utc);

    [Fact]
    public async Task RefreshAsync_WellFormedSessionFixture_AggregatesAsExpected()
    {
        InstallFixture("well-formed-session.jsonl");
        var store = CreateStore(new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));

        var added = await store.RefreshAsync(CancellationToken.None);

        // 5 lines: 1 user record (skipped), 3 distinct assistant ids, 1 exact duplicate id ignored.
        Assert.Equal(3, added);
        Assert.Equal(3, store.AllTotals.Messages);
        Assert.Equal(0, store.MalformedRecordCount);
        Assert.Equal(400, store.AllTotals.UnpricedTokens); // resp_003 (gpt-5): 300 + 100
        Assert.Equal(0.0295m, store.AllTotals.CostUsd); // resp_001 (Opus 5) 0.0175 + resp_002 (Sonnet 5) 0.012
        Assert.Equal(["gpt-5"], store.UnpricedInRange(14));

        var projects = store.Breakdown("project", UsageMetric.TotalTokens, 14);
        Assert.Contains(projects, entry => entry.Label == "project-alpha");
        Assert.Contains(projects, entry => entry.Label == "project-beta");
    }

    [Fact]
    public async Task RefreshAsync_MalformedSessionFixture_SkipsBadRecordsAndKeepsTheGoodOnes()
    {
        InstallFixture("malformed-session.jsonl");
        var store = CreateStore(new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, added);
        Assert.Equal(2, store.AllTotals.Messages);
        // One bad token count + one bad timestamp = 2 malformed; the corrupt JSON line is silently
        // skipped and does not add to this count (see ClaudeTranscriptStoreFileTests for that rule
        // exercised in isolation).
        Assert.Equal(2, store.MalformedRecordCount);
        Assert.Null(store.LastDirectoryError);
    }
}
