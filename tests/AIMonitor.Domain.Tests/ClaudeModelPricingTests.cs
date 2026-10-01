using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class ClaudeModelPricingTests
{
    [Fact]
    public void IsPriced_KnownModel_IsTrue() => Assert.True(ClaudeModelPricing.IsPriced("claude-opus-5"));

    [Fact]
    public void IsPriced_DatedSnapshotOfKnownModel_IsTrue() =>
        Assert.True(ClaudeModelPricing.IsPriced("claude-haiku-4-5-20251001"));

    [Fact]
    public void PriceKind_DatedSnapshotOfKnownModel_IsExact() =>
        Assert.Equal(PricingKind.Exact, ClaudeModelPricing.PriceKind("claude-haiku-4-5-20251001"));

    [Fact]
    public void IsPriced_FuturePointReleaseInKnownFamily_IsTrueButOnlyEstimated()
    {
        // Priced, yes - but the caller has to be able to tell it apart: the whole point of "no
        // published price" is that the prefix table hands one over for any id in a known family.
        Assert.True(ClaudeModelPricing.IsPriced("claude-opus-9-9"));
        Assert.Equal(PricingKind.Estimated, ClaudeModelPricing.PriceKind("claude-opus-9-9"));
    }

    [Fact]
    public void IsPriced_NewFamily_IsFalse() => Assert.False(ClaudeModelPricing.IsPriced("claude-newfamily-1"));

    [Fact]
    public void IsPriced_ForeignModel_IsFalse() => Assert.False(ClaudeModelPricing.IsPriced("gpt-5"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsPriced_EmptyOrNullModel_IsFalse(string? model) => Assert.False(ClaudeModelPricing.IsPriced(model));

    [Fact]
    public void PriceKind_PublishedModel_IsExact() =>
        Assert.Equal(PricingKind.Exact, ClaudeModelPricing.PriceKind("claude-opus-5"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gpt-5")]
    public void PriceKind_UnknownFamily_IsUnknown(string? model) =>
        Assert.Equal(PricingKind.Unknown, ClaudeModelPricing.PriceKind(model));

    [Fact]
    public void RateFor_SonnetFamilyRateDiffersFromSonnet5_SoTheEstimateCanBeWrong()
    {
        // Not hypothetical rounding: the published input price dropped between 4.6 and 5, so a
        // future claude-sonnet-7 is billed by the family prefix at the *older* pair, not the
        // family's most recent published rate. The guess can be out by a real amount, in either
        // direction.
        var older = ClaudeModelPricing.RateFor("claude-sonnet-4-6");
        var newer = ClaudeModelPricing.RateFor("claude-sonnet-5");
        var guessed = ClaudeModelPricing.RateFor("claude-sonnet-7");

        Assert.NotEqual(
            (older.InputPerMillion, older.OutputPerMillion), (newer.InputPerMillion, newer.OutputPerMillion));
        Assert.Equal(PricingKind.Estimated, ClaudeModelPricing.PriceKind("claude-sonnet-7"));
        Assert.NotEqual(
            (guessed.InputPerMillion, guessed.OutputPerMillion), (newer.InputPerMillion, newer.OutputPerMillion));
        Assert.Equal(
            (older.InputPerMillion, older.OutputPerMillion), (guessed.InputPerMillion, guessed.OutputPerMillion));
    }

    [Fact]
    public void RateFor_EstimatedModel_IsPositiveNotZero()
    {
        // It must still contribute to the total, or the equivalent-value figure collapses.
        var rate = ClaudeModelPricing.RateFor("claude-opus-9-9");
        Assert.True(rate.InputPerMillion > 0);
        Assert.True(rate.OutputPerMillion > 0);
    }

    [Fact]
    public void RateFor_UnknownModel_IsZero()
    {
        var rate = ClaudeModelPricing.RateFor("gpt-5");
        Assert.Equal(0m, rate.InputPerMillion);
        Assert.Equal(0m, rate.OutputPerMillion);
    }

    [Fact]
    public void DisplayName_KnownModel_IsHumanReadable() =>
        Assert.Equal("Opus 5", ClaudeModelPricing.DisplayName("claude-opus-5"));

    [Fact]
    public void DisplayName_EstimatedModel_IsTheFamilyLabel() =>
        Assert.Equal("Opus", ClaudeModelPricing.DisplayName("claude-opus-9-9"));

    [Fact]
    public void DisplayName_UnknownModel_EchoesTheModelId() =>
        Assert.Equal("gpt-5", ClaudeModelPricing.DisplayName("gpt-5"));

    [Fact]
    public void DisplayName_EmptyModel_IsUnknown() =>
        Assert.Equal("Unknown", ClaudeModelPricing.DisplayName(""));

    [Fact]
    public void DisplayName_NullModel_IsUnknown() =>
        Assert.Equal("Unknown", ClaudeModelPricing.DisplayName(null));

    [Fact]
    public void Cost_UnknownModel_IsZero_SoItAddsNoMoney() =>
        Assert.Equal(0m, ClaudeModelPricing.Cost("gpt-5", inputTokens: 1_000_000, outputTokens: 1_000_000));

    [Fact]
    public void Cost_KnownModel_MatchesPublishedInputAndOutputRate()
    {
        // claude-opus-5 lists at $5/$25 per million tokens.
        var cost = ClaudeModelPricing.Cost("claude-opus-5", inputTokens: 1_000_000, outputTokens: 1_000_000);
        Assert.Equal(30.00m, cost);
    }

    [Fact]
    public void Cost_CacheMultipliers_MatchThePublishedFormula()
    {
        // claude-sonnet-5 input rate is $2/M; 5-minute cache write is 1.25x, 1-hour write is 2x,
        // and a cache read is 0.1x that input rate.
        var cost = ClaudeModelPricing.Cost(
            "claude-sonnet-5", cacheWrite5mTokens: 1_000_000, cacheWrite1hTokens: 1_000_000, cacheReadTokens: 1_000_000);

        Assert.Equal(2.00m * 1.25m + 2.00m * 2.0m + 2.00m * 0.1m, cost);
    }

    [Fact]
    public void Cost_NoTokens_IsZero() => Assert.Equal(0m, ClaudeModelPricing.Cost("claude-opus-5"));

    [Theory]
    [InlineData("claude-opus-5", "claude-opus-5-20251001")]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4-6-20250815")]
    public void PriceKind_TrailingEightDigitDateSuffix_IsTrimmedAndMatchesTheExactModel(
        string baseModel, string datedModel)
    {
        Assert.Equal(ClaudeModelPricing.PriceKind(baseModel), ClaudeModelPricing.PriceKind(datedModel));
        Assert.Equal(PricingKind.Exact, ClaudeModelPricing.PriceKind(datedModel));
        Assert.Equal(ClaudeModelPricing.DisplayName(baseModel), ClaudeModelPricing.DisplayName(datedModel));
    }

    [Fact]
    public void PriceKind_SevenDigitTrailingRun_IsNotTreatedAsADateSuffix()
    {
        // Only an exact 8-digit trailing run is a date snapshot; a 7-digit tail is just part of an
        // unrecognised id and must not accidentally resolve to a family prefix through the date path.
        Assert.Equal(PricingKind.Unknown, ClaudeModelPricing.PriceKind("claude-newfamily-1234567"));
    }

    [Fact]
    public void PriceKind_NineDigitTrailingRun_IsNotTreatedAsADateSuffix() =>
        Assert.Equal(PricingKind.Unknown, ClaudeModelPricing.PriceKind("claude-newfamily-123456789"));

    [Fact]
    public void PriceKind_NonDigitEightCharacterTail_StillMatchesThePrefixOnTheFullId() =>
        Assert.Equal(PricingKind.Estimated, ClaudeModelPricing.PriceKind("claude-opus-abcdefgh"));

    [Fact]
    public void PriceKind_ModelWithNoHyphen_DoesNotThrow() =>
        Assert.Equal(PricingKind.Unknown, ClaudeModelPricing.PriceKind("nohyphens"));
}
