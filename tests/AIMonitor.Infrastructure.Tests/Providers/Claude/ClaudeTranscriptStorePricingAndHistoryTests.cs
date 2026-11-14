using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Exercises how <see cref="ClaudeTranscriptStore"/> combines ingestion with
/// <see cref="ClaudeModelPricing"/> (PAR-007: EXACT/ESTIMATED/UNKNOWN) and how its window/breakdown/
/// history queries (PAR-006) scope to a range.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStorePricingAndHistoryTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-transcript-pricing-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _project;

    public ClaudeTranscriptStorePricingAndHistoryTests()
    {
        _project = Path.Combine(_root, "C--work-project");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ClaudeTranscriptStore CreateStore() => new(_root, new FakeClock(FixedNow), TimeZoneInfo.Utc);

    private static string RecordJson(string model, long inputTokens, string messageId, DateTimeOffset timestamp) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model
        + "\",\"usage\":{\"input_tokens\":" + inputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"output_tokens\":0}}}";

    private void Write(params string[] lines) =>
        File.WriteAllText(Path.Combine(_project, "session.jsonl"), string.Join("\n", lines) + "\n");

    [Fact]
    public async Task WindowTotals_FamilyEstimate_IsCountedApartFromBothUnpricedAndExact()
    {
        Write(RecordJson("claude-opus-9-9", 1_000_000, "m1", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var totals = store.WindowTotals(14);

        Assert.Equal(0, totals.UnpricedTokens);
        Assert.Equal(1_000_000, totals.EstimatedTokens);
        Assert.True(totals.CostUsd > 0);
        Assert.Empty(store.UnpricedInRange(14));
        Assert.Equal([ClaudeModelPricing.DisplayName("claude-opus-9-9")], store.EstimatedInRange(14));
    }

    [Fact]
    public async Task WindowTotals_PricedModel_CountsNothingAsUnpricedOrEstimated()
    {
        Write(RecordJson("claude-opus-5", 1_000, "m1", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var totals = store.WindowTotals(14);
        Assert.Equal(0, totals.UnpricedTokens);
        Assert.True(totals.CostUsd > 0);
        Assert.Empty(store.UnpricedInRange(14));
        Assert.Equal(0, totals.EstimatedTokens);
        Assert.Empty(store.EstimatedInRange(14));
    }

    [Fact]
    public async Task WindowTotals_UnknownModel_IsCountedAndNamed_AndAddsNoMoney()
    {
        Write(RecordJson("gpt-5", 2_000, "m1", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var totals = store.WindowTotals(14);
        Assert.Equal(2_000, totals.UnpricedTokens);
        Assert.Equal(0m, totals.CostUsd);
        Assert.Equal(["gpt-5"], store.UnpricedInRange(14));
    }

    [Fact]
    public async Task WindowTotals_MixedRange_PricedPortionIsStillRight()
    {
        Write(
            RecordJson("claude-opus-5", 1_000_000, "m1", FixedNow),
            RecordJson("gpt-5", 500, "m2", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var totals = store.WindowTotals(14);
        Assert.Equal(500, totals.UnpricedTokens);
        Assert.Equal(5.00m, totals.CostUsd);
    }

    [Fact]
    public async Task UnpricedInRange_ModelOutsideTheRange_IsNotNamed()
    {
        Write(
            RecordJson("old-model-a", 1_000, "old", FixedNow.AddDays(-40)),
            RecordJson("new-model-b", 1_000, "new", FixedNow.AddDays(-1)));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(["new-model-b"], store.UnpricedInRange(7));
        Assert.Equal(["new-model-b", "old-model-a"], store.UnpricedInRange(90));
    }

    [Fact]
    public async Task UnpricedInRange_NamesAndTotalsDescribeTheSameRange()
    {
        Write(
            RecordJson("old-model-a", 1_000, "old", FixedNow.AddDays(-40)),
            RecordJson("new-model-b", 1_000, "new", FixedNow.AddDays(-1)));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var totals = store.WindowTotals(7);
        var names = store.UnpricedInRange(7);

        Assert.Equal(1_000, totals.UnpricedTokens);
        Assert.Single(names);
    }

    [Fact]
    public async Task UnpricedInRange_PricedModel_IsNeverNamed()
    {
        Write(RecordJson("claude-opus-5", 1_000, "m1", FixedNow.AddDays(-1)));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        Assert.Empty(store.UnpricedInRange(7));
    }

    [Fact]
    public async Task Breakdown_OrdersDescendingByValue()
    {
        Write(
            RecordJson("claude-opus-5", 100, "m1", FixedNow),
            RecordJson("claude-sonnet-5", 500, "m2", FixedNow),
            RecordJson("claude-haiku-4-5", 50, "m3", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var breakdown = store.Breakdown("model", UsageMetric.TotalTokens, 14);

        Assert.Equal(["Sonnet 5", "Opus 5", "Haiku 4.5"], breakdown.Select(entry => entry.Label));
    }

    [Fact]
    public async Task Breakdown_TiedValues_BreakOrdinallyByLabel()
    {
        Write(
            RecordJson("claude-opus-5", 100, "m1", FixedNow),
            RecordJson("claude-haiku-4-5", 100, "m2", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var breakdown = store.Breakdown("model", UsageMetric.TotalTokens, 14);

        // Both total 100 tokens; "Haiku 4.5" sorts before "Opus 5" ordinally.
        Assert.Equal(["Haiku 4.5", "Opus 5"], breakdown.Select(entry => entry.Label));
    }

    [Fact]
    public async Task Breakdown_EquivalentValueMetric_OrdersByCostRatherThanTokenCount()
    {
        // gpt-5 has more raw tokens but is unpriced (zero cost); claude-opus-5 has fewer tokens but
        // a real published rate, so under the Equivalent value metric it must rank first.
        Write(
            RecordJson("gpt-5", 1_000_000, "m1", FixedNow),
            RecordJson("claude-opus-5", 1_000, "m2", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var breakdown = store.Breakdown("model", UsageMetric.EquivalentValue, 14);

        // gpt-5 contributes zero cost (PAR-007: UNKNOWN adds no money) and drops out entirely rather
        // than appearing with an invented zero-cost row.
        Assert.Equal("Opus 5", Assert.Single(breakdown).Label);
    }

    [Fact]
    public async Task BuildHistory_MoreThanEightModels_FoldsTheSmallestIntoOther()
    {
        var lines = Enumerable.Range(0, 9)
            .Select(n => RecordJson($"model-{n}", (9 - n) * 100, $"m{n}", FixedNow))
            .ToArray();
        Write(lines);
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var history = store.BuildHistory(14, UsageMetric.TotalTokens);

        // 9 distinct models: the top 7 by total plus "Other" = 8 series entries; the two smallest
        // (model-7 at 200 tokens, model-8 at 100) fold into "Other".
        Assert.Equal(8, history.Series.Count);
        Assert.Contains("Other", history.Series);
        Assert.DoesNotContain("model-7", history.Series);
        Assert.DoesNotContain("model-8", history.Series);
        Assert.Contains("model-0", history.Series);

        var bucket = history.Buckets.Single(b => b.Day == DateOnly.FromDateTime(FixedNow.UtcDateTime));
        Assert.Equal(300.0, bucket.PerModel["Other"]);
    }

    [Fact]
    public async Task BuildHistory_EightOrFewerModels_KeepsThemAllNamed()
    {
        var lines = Enumerable.Range(0, 8)
            .Select(n => RecordJson($"model-{n}", 100, $"m{n}", FixedNow))
            .ToArray();
        Write(lines);
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var history = store.BuildHistory(14, UsageMetric.TotalTokens);

        Assert.Equal(8, history.Series.Count);
        Assert.DoesNotContain("Other", history.Series);
    }

    [Fact]
    public async Task BuildHistory_EmptyStore_ReturnsAnEmptyButValidHistory()
    {
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var history = store.BuildHistory(14, UsageMetric.TotalTokens);

        Assert.Empty(history.Series);
        Assert.Equal(14, history.Buckets.Count);
        Assert.All(history.Buckets, bucket => Assert.Equal(0.0, bucket.Total));
        Assert.Empty(history.ByModel);
        Assert.Empty(history.ByProject);
    }

    [Fact]
    public async Task BuildHistory_EmptyOrUnrecognisedMetric_DefaultsToTotalTokens()
    {
        Write(RecordJson("claude-opus-5", 1_000, "m1", FixedNow));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var history = store.BuildHistory(14, "");

        Assert.Equal(UsageMetric.TotalTokens, history.Metric);
    }

    [Fact]
    public void WindowTotals_NonPositiveDays_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore().WindowTotals(0));

    [Fact]
    public void BuildHistory_NonPositiveDays_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore().BuildHistory(0, UsageMetric.TotalTokens));

    [Fact]
    public void UnpricedInRange_NonPositiveDays_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore().UnpricedInRange(-1));

    [Fact]
    public void Breakdown_NonPositiveDays_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore().Breakdown("model", UsageMetric.TotalTokens, 0));
}
