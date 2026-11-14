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
        // Rewritten for P2a (Docs/SPEC_R014.md §P2a): the prefix fallbacks moved to the current
        // generation — "sonnet 2/10/0.10" — so a future claude-sonnet-7 now matches claude-sonnet-5
        // on input and output (spec row: claude-sonnet-5 | 2 | 10) but still misses its cache read
        // (prefix 0.10 vs published 0.20). The estimate can still be out by a real amount — now on
        // the cache-read leg. And the family's published rows differ (claude-sonnet-4-6 3/15/0.30
        // vs claude-sonnet-5 2/10/0.20), so pinning the prefix to any one row stays a guess.
        var older = ClaudeModelPricing.RateFor("claude-sonnet-4-6");
        var newer = ClaudeModelPricing.RateFor("claude-sonnet-5");
        var guessed = ClaudeModelPricing.RateFor("claude-sonnet-7");

        Assert.Equal(PricingKind.Estimated, ClaudeModelPricing.PriceKind("claude-sonnet-7"));
        Assert.Equal(newer.InputPerMillion, guessed.InputPerMillion);      // spec: prefix sonnet input 2
        Assert.Equal(newer.OutputPerMillion, guessed.OutputPerMillion);    // spec: prefix sonnet output 10
        Assert.Equal(0.10m, guessed.CacheReadPerMillion);                  // spec: prefix sonnet read 0.10
        Assert.Equal(0.20m, newer.CacheReadPerMillion);                    // spec row claude-sonnet-5 read 0.20
        Assert.NotEqual(newer.CacheReadPerMillion, guessed.CacheReadPerMillion);
        Assert.NotEqual(
            (older.InputPerMillion, older.OutputPerMillion), (newer.InputPerMillion, newer.OutputPerMillion));
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
        // Rewritten for P2a (Docs/SPEC_R014.md §P2a + decision A8): only the *writes* are multipliers
        // of the input rate — "5m write stays ×1.25 and 1h write ×2.0" — while cache read is now an
        // explicit per-model rate ("Replace the fixed cache-read multiplier with per-model rates").
        // claude-sonnet-5 lists input 2 / cache read 0.20 (table row "2 | 10 | 0.20 | unchanged").
        // Hand calc: 1M×(2.00×1.25 = 2.50) + 1M×(2.00×2.0 = 4.00) + 1M×0.20 = 2.50 + 4.00 + 0.20 = 6.70.
        var cost = ClaudeModelPricing.Cost(
            "claude-sonnet-5", cacheWrite5mTokens: 1_000_000, cacheWrite1hTokens: 1_000_000, cacheReadTokens: 1_000_000);

        Assert.Equal(6.70m, cost);
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
