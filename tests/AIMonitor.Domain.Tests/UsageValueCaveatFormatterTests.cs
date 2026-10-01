using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class UsageValueCaveatFormatterTests
{
    [Fact]
    public void Format_NothingUnpricedOrEstimated_ReadsExactlyAsItAlwaysDid() =>
        Assert.Equal("at API list price", UsageValueCaveatFormatter.Format(0, [], 0, []));

    [Fact]
    public void Format_FamilyEstimate_IsDisclosedWithoutClaimingNoPublishedPrice()
    {
        var caveat = UsageValueCaveatFormatter.Format(3_000_000, ["Opus"], 0, []);

        Assert.Contains("at API list price", caveat);
        Assert.Contains("3M tokens", caveat);
        Assert.Contains("Opus", caveat);
        Assert.Contains("family", caveat);
        Assert.DoesNotContain("no published price", caveat);
    }

    [Fact]
    public void Format_OneUnknownModel_IsNamedAndFlaggedAsNoPublishedPrice()
    {
        var caveat = UsageValueCaveatFormatter.Format(0, [], 12_400_000, ["gpt-5"]);

        Assert.Contains("at API list price", caveat);
        Assert.Contains("12.4M tokens", caveat);
        Assert.Contains("gpt-5", caveat);
        Assert.Contains("no published price", caveat);
    }

    [Fact]
    public void Format_BothKindsOfDoubt_AreReportedSeparately()
    {
        var caveat = UsageValueCaveatFormatter.Format(9_000, ["Sonnet"], 500, ["gpt-5"]);

        Assert.Contains("excludes", caveat);
        Assert.Contains("gpt-5", caveat);
        Assert.Contains("estimates", caveat);
        Assert.Contains("Sonnet", caveat);
    }

    [Fact]
    public void Format_TwoUnpricedModels_AreBothNamed()
    {
        var caveat = UsageValueCaveatFormatter.Format(0, [], 1_000, ["gpt-5", "claude-newfamily-1"]);

        Assert.Contains("gpt-5", caveat);
        Assert.Contains("claude-newfamily-1", caveat);
    }

    [Fact]
    public void Format_ManyUnpricedModels_AreCountedRatherThanListed()
    {
        // A caption is one line; five model ids would not fit on it.
        var models = Enumerable.Range(0, 5).Select(n => $"model-{n}").ToArray();

        var caveat = UsageValueCaveatFormatter.Format(0, [], 1_000, models);

        Assert.Contains("5 models", caveat);
        Assert.DoesNotContain("model-0", caveat);
    }

    [Fact]
    public void Format_ManyEstimatedModels_AreCountedRatherThanListed()
    {
        var models = Enumerable.Range(0, 4).Select(n => $"m{n}").ToArray();

        var caveat = UsageValueCaveatFormatter.Format(1, models, 0, []);

        Assert.Contains("4 models", caveat);
    }

    [Fact]
    public void Format_ExactlyTwoModels_AreListedByNameRatherThanCollapsedToACount()
    {
        var caveat = UsageValueCaveatFormatter.Format(0, [], 1_000, ["alpha", "beta"]);

        Assert.Contains("alpha and beta", caveat);
    }

    [Theory]
    [InlineData(500, "500 tokens")]
    [InlineData(1_000, "1K tokens")]
    [InlineData(3_000_000, "3M tokens")]
    [InlineData(12_400_000, "12.4M tokens")]
    public void Format_TokenCountFormatting_UsesExpectedMagnitudeSuffix(long tokens, string expectedFragment)
    {
        var caveat = UsageValueCaveatFormatter.Format(0, [], tokens, ["gpt-5"]);
        Assert.Contains(expectedFragment, caveat);
    }

    [Fact]
    public void Format_NullEstimatedModels_Throws() =>
        Assert.Throws<ArgumentNullException>(() => UsageValueCaveatFormatter.Format(1, null!, 0, []));

    [Fact]
    public void Format_NullUnpricedModels_Throws() =>
        Assert.Throws<ArgumentNullException>(() => UsageValueCaveatFormatter.Format(0, [], 1, null!));
}
