using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

/// <summary>
/// P2a — Claude pricing, written from <c>Docs/SPEC_R014.md</c> only (AI_WORKFLOW §5.6: the
/// implementation is not read first). Covers the §P2a price table, decision A8 (5m write =
/// input × 1.25, 1h write = input × 2.0 for every model), decisions A9/A13 (prefix fallbacks have
/// one tier only; the &gt;100k tier applies only to exact <c>claude-haiku-5-5</c> and its 8-digit
/// date-snapshot form, applied through <c>Cost</c> only) and OP4 (cost needs the per-record prompt
/// size, so <c>Cost</c> decides the tier from the token counts it receives).
/// Every expected value cites the spec line or shows the hand calculation.
/// Decimal table values use MemberData because the C# compiler rejects decimal literals as
/// attribute arguments (CS0182); the values themselves are unchanged.
/// These tests do not compile until the P2a branch merges (new <see cref="ClaudeModelRate"/>
/// constructor and per-record Haiku tier); that is expected for a parallel Test Author handoff.
/// </summary>
public class ClaudeModelPricingP2aTests
{
    // ------------------------------------------------------------------
    // ClaudeModelRate — P2a constructor (handoff API:
    // ClaudeModelRate(inputPerMillion, outputPerMillion, displayName, cacheReadPerMillion);
    // negative cache read → ArgumentOutOfRangeException).
    // ------------------------------------------------------------------

    [Fact]
    public void ClaudeModelRate_CacheReadIsAnExplicitConstructorValue_WritesDeriveFromInput()
    {
        // Handoff API + A8. Hand calc: 10.00 × 1.25 = 12.50 (5m), 10.00 × 2.0 = 20.00 (1h).
        var rate = new ClaudeModelRate(
            inputPerMillion: 10.00m, outputPerMillion: 50.00m, displayName: "Fable 5.1", cacheReadPerMillion: 0.25m);

        Assert.Equal(10.00m, rate.InputPerMillion);
        Assert.Equal(50.00m, rate.OutputPerMillion);
        Assert.Equal(0.25m, rate.CacheReadPerMillion);
        Assert.Equal("Fable 5.1", rate.DisplayName);
        Assert.Equal(12.50m, rate.CacheWrite5mPerMillion);
        Assert.Equal(20.00m, rate.CacheWrite1hPerMillion);
    }

    [Fact]
    public void ClaudeModelRate_NegativeCacheRead_Throws()
    {
        // Handoff API: "negative cache read → ArgumentOutOfRangeException".
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClaudeModelRate(inputPerMillion: 1.00m, outputPerMillion: 1.00m, displayName: "Test", cacheReadPerMillion: -0.01m));
    }

    // ------------------------------------------------------------------
    // Spec §P2a table — every exact row. Values are (input, output, cache read) per million
    // tokens; the 5m/1h columns are the A8 multipliers of the input rate, hand-computed per row
    // (e.g. claude-opus-4-1: 15.00 × 1.25 = 18.75, 15.00 × 2.0 = 30.00).
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> SpecExactRows()
    {
        // (model, input, output, cache read, cacheWrite5m, cacheWrite1h) — one yield per table row,
        // the spec line quoted after each value.
        yield return new object[] { "claude-fable-5-1", 10.00m, 50.00m, 0.25m, 12.50m, 20.00m };   // "10 | 50 | **0.25** | was 1.00 (×0.1)"
        yield return new object[] { "claude-mythos-5-1", 10.00m, 50.00m, 0.25m, 12.50m, 20.00m };  // "10 | 50 | **0.25**"
        yield return new object[] { "claude-fable-5", 10.00m, 50.00m, 1.00m, 12.50m, 20.00m };     // "claude-fable-5 / claude-mythos-5 | 10 | 50 | 1.00 | unchanged"
        yield return new object[] { "claude-mythos-5", 10.00m, 50.00m, 1.00m, 12.50m, 20.00m };    // table: same row as claude-fable-5
        yield return new object[] { "claude-opus-5-5", 4.00m, 20.00m, 0.20m, 5.00m, 8.00m };       // "**4** | **20** | **0.20** | new; was prefix fallback 5/25/0.50 Estimated"
        yield return new object[] { "claude-opus-5", 5.00m, 25.00m, 0.50m, 6.25m, 10.00m };        // "claude-opus-5, 4-8, 4-7, 4-6 | 5 | 25 | 0.50 | unchanged"
        yield return new object[] { "claude-opus-4-8", 5.00m, 25.00m, 0.50m, 6.25m, 10.00m };      // table: same row as claude-opus-5
        yield return new object[] { "claude-opus-4-7", 5.00m, 25.00m, 0.50m, 6.25m, 10.00m };      // table: same row as claude-opus-5
        yield return new object[] { "claude-opus-4-6", 5.00m, 25.00m, 0.50m, 6.25m, 10.00m };      // table: same row as claude-opus-5
        yield return new object[] { "claude-opus-4-5", 5.00m, 25.00m, 0.50m, 6.25m, 10.00m };      // "5 | 25 | 0.50 | new"
        yield return new object[] { "claude-opus-4-1", 15.00m, 75.00m, 1.50m, 18.75m, 30.00m };    // "claude-opus-4-1, claude-opus-4 | **15** | **75** | 1.50 | new; was prefix 5/25 Estimated"
        yield return new object[] { "claude-opus-4", 15.00m, 75.00m, 1.50m, 18.75m, 30.00m };      // table: same row as claude-opus-4-1
        yield return new object[] { "claude-sonnet-5-5", 2.00m, 10.00m, 0.10m, 2.50m, 4.00m };     // "2 | 10 | **0.10** | new; was prefix 3/15/0.30 Estimated"
        yield return new object[] { "claude-sonnet-5", 2.00m, 10.00m, 0.20m, 2.50m, 4.00m };       // "2 | 10 | 0.20 | unchanged"
        yield return new object[] { "claude-sonnet-4-6", 3.00m, 15.00m, 0.30m, 3.75m, 6.00m };     // "claude-sonnet-4-6, 4-5, claude-sonnet-4 | 3 | 15 | 0.30 | 4-5 and 4 new"
        yield return new object[] { "claude-sonnet-4-5", 3.00m, 15.00m, 0.30m, 3.75m, 6.00m };     // table: same row as claude-sonnet-4-6
        yield return new object[] { "claude-sonnet-4", 3.00m, 15.00m, 0.30m, 3.75m, 6.00m };       // table: same row as claude-sonnet-4-6
        // "0.10 / **0.50** | 0.50 / **2.50** | 0.01 / **0.05** | second value when the request's prompt
        // > 100,000 tokens" — the normal tier is what RateFor exposes; the high tier exists only inside
        // Cost (handoff: "applied only through Cost").
        yield return new object[] { "claude-haiku-5-5", 0.10m, 0.50m, 0.01m, 0.125m, 0.20m };
        yield return new object[] { "claude-haiku-4-5", 1.00m, 5.00m, 0.10m, 1.25m, 2.00m };       // "1 | 5 | 0.10 | unchanged"
    }

    [Theory]
    [MemberData(nameof(SpecExactRows))]
    public void ExactRateTable_Row_ResolvesExactWithSpecValues(
        string model, decimal input, decimal output, decimal cacheRead, decimal cacheWrite5m, decimal cacheWrite1h)
    {
        Assert.Equal(PricingKind.Exact, ClaudeModelPricing.PriceKind(model));

        var rate = ClaudeModelPricing.RateFor(model);
        Assert.Equal(input, rate.InputPerMillion);
        Assert.Equal(output, rate.OutputPerMillion);
        Assert.Equal(cacheRead, rate.CacheReadPerMillion);
        Assert.Equal(cacheWrite5m, rate.CacheWrite5mPerMillion);
        Assert.Equal(cacheWrite1h, rate.CacheWrite1hPerMillion);
    }

    // A date snapshot (exact id + "-" + 8 digits) resolves to the same row (existing documented
    // behaviour: "Published for this exact model id (or its id with a trailing date snapshot
    // trimmed)"). Assertions are relative to the base id, whose absolute values are pinned above.

    [Theory]
    [InlineData("claude-fable-5-1", "claude-fable-5-1-20260301")]
    [InlineData("claude-mythos-5-1", "claude-mythos-5-1-20260501")]
    [InlineData("claude-fable-5", "claude-fable-5-20260101")]
    [InlineData("claude-mythos-5", "claude-mythos-5-20260210")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5-20260201")]
    [InlineData("claude-opus-4-5", "claude-opus-4-5-20251101")]
    [InlineData("claude-opus-4-1", "claude-opus-4-1-20250805")]
    [InlineData("claude-opus-4", "claude-opus-4-20250514")]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-20260415")]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4-5-20250929")]
    [InlineData("claude-sonnet-4", "claude-sonnet-4-20250514")]
    [InlineData("claude-haiku-5-5", "claude-haiku-5-5-20261101")]
    public void DateSnapshotOfSpecRow_ResolvesExactWithTheSameValues(string baseModel, string datedModel)
    {
        Assert.Equal(PricingKind.Exact, ClaudeModelPricing.PriceKind(datedModel));

        var baseRate = ClaudeModelPricing.RateFor(baseModel);
        var datedRate = ClaudeModelPricing.RateFor(datedModel);
        Assert.Equal(baseRate.InputPerMillion, datedRate.InputPerMillion);
        Assert.Equal(baseRate.OutputPerMillion, datedRate.OutputPerMillion);
        Assert.Equal(baseRate.CacheReadPerMillion, datedRate.CacheReadPerMillion);
        Assert.Equal(baseRate.CacheWrite5mPerMillion, datedRate.CacheWrite5mPerMillion);
        Assert.Equal(baseRate.CacheWrite1hPerMillion, datedRate.CacheWrite1hPerMillion);
        Assert.Equal(ClaudeModelPricing.DisplayName(baseModel), ClaudeModelPricing.DisplayName(datedModel));
    }

    // ------------------------------------------------------------------
    // Prefix fallbacks — spec §P2a: "Prefix fallbacks stay Estimated, with values updated to the
    // current generation: opus 4/20/0.20, sonnet 2/10/0.10, haiku 0.10/0.50/0.01,
    // fable 10/50/0.25, mythos 10/50/0.25." 5m/1h are the A8 multipliers (e.g. opus:
    // 4.00 × 1.25 = 5.00, 4.00 × 2.0 = 8.00). A9/A13: one tier only — tested per family below.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> SpecPrefixRows()
    {
        // (model, input, output, cache read, cacheWrite5m, cacheWrite1h) — ids not in the exact
        // table, resolving through the family prefix; the "claude-<family>-9-9" shape mirrors the
        // existing estimated-id convention (claude-opus-9-9).
        yield return new object[] { "claude-fable-9-9", 10.00m, 50.00m, 0.25m, 12.50m, 20.00m };   // prefix "fable 10/50/0.25"
        yield return new object[] { "claude-mythos-9-9", 10.00m, 50.00m, 0.25m, 12.50m, 20.00m };  // prefix "mythos 10/50/0.25"
        yield return new object[] { "claude-opus-9-9", 4.00m, 20.00m, 0.20m, 5.00m, 8.00m };       // prefix "opus 4/20/0.20"
        yield return new object[] { "claude-sonnet-9-9", 2.00m, 10.00m, 0.10m, 2.50m, 4.00m };     // prefix "sonnet 2/10/0.10"
        yield return new object[] { "claude-haiku-9-9", 0.10m, 0.50m, 0.01m, 0.125m, 0.20m };      // prefix "haiku 0.10/0.50/0.01"
    }

    [Theory]
    [MemberData(nameof(SpecPrefixRows))]
    public void PrefixFallback_ResolvesEstimatedWithCurrentGenerationValues(
        string model, decimal input, decimal output, decimal cacheRead, decimal cacheWrite5m, decimal cacheWrite1h)
    {
        Assert.Equal(PricingKind.Estimated, ClaudeModelPricing.PriceKind(model));

        var rate = ClaudeModelPricing.RateFor(model);
        Assert.Equal(input, rate.InputPerMillion);
        Assert.Equal(output, rate.OutputPerMillion);
        Assert.Equal(cacheRead, rate.CacheReadPerMillion);
        Assert.Equal(cacheWrite5m, rate.CacheWrite5mPerMillion);
        Assert.Equal(cacheWrite1h, rate.CacheWrite1hPerMillion);
    }

    // ------------------------------------------------------------------
    // Cache write multipliers through Cost — A8: "5m = input × 1.25, 1h = input × 2.0 for every
    // model in the table". Cache read is NOT a multiplier any more (spec §P2a intro: "Replace the
    // fixed cache-read multiplier with per-model rates").
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> SpecCacheWriteCostRows()
    {
        // (model, expected cost of 1M 5m-write, 1M 1h-write, 1M cache-read, total of all three)
        // claude-sonnet-5 table row 2/10/0.20 → 5m 2×1.25=2.50, 1h 2×2=4.00; hand calc:
        //   (1M×2.50 + 1M×4.00 + 1M×0.20)/1M = 6.70.
        yield return new object[] { "claude-sonnet-5", 2.50m, 4.00m, 0.20m, 6.70m };
        // claude-sonnet-5-5 table row 2/10/0.10 → hand calc: 2.50 + 4.00 + 0.10 = 6.60. The old
        //   ×0.1 rule priced this read at 0.20 (total 6.70); pinning 6.60 proves the read rate is
        //   explicit, not derived.
        yield return new object[] { "claude-sonnet-5-5", 2.50m, 4.00m, 0.10m, 6.60m };
        // claude-opus-4-1 table row 15/75/1.50 → 5m 15×1.25=18.75, 1h 15×2=30; hand calc:
        //   18.75 + 30.00 + 1.50 = 50.25.
        yield return new object[] { "claude-opus-4-1", 18.75m, 30.00m, 1.50m, 50.25m };
    }

    [Theory]
    [MemberData(nameof(SpecCacheWriteCostRows))]
    public void Cost_OneMillionOfEachCacheTokenType_PricesWritesByA8AndReadByTheExplicitRate(
        string model, decimal write5m, decimal write1h, decimal cacheRead, decimal total)
    {
        // Per-row hand calc: each leg = 1,000,000 × rate / 1,000,000 = rate; the total is the sum
        // of the three legs (hand calcs are in the data rows above).
        Assert.Equal(write5m, ClaudeModelPricing.Cost(model, cacheWrite5mTokens: 1_000_000));
        Assert.Equal(write1h, ClaudeModelPricing.Cost(model, cacheWrite1hTokens: 1_000_000));
        Assert.Equal(cacheRead, ClaudeModelPricing.Cost(model, cacheReadTokens: 1_000_000));
        Assert.Equal(total, ClaudeModelPricing.Cost(
            model, cacheWrite5mTokens: 1_000_000, cacheWrite1hTokens: 1_000_000, cacheReadTokens: 1_000_000));
    }

    // ------------------------------------------------------------------
    // Haiku 5.5 > 100k tier — spec §P2a: "second value when the request's prompt > 100,000
    // tokens"; "Haiku 5.5 tier is per request: prompt = input + cache creation + cache read of
    // that one transcript record; > 100,000 → all of that record's tokens at the higher rates.
    // This forces cost to be computed per record at ingest" (OP4). Handoff: the tier is "applied
    // only through Cost", at 100,000 (low) and 100,001 (high), "prompt = input + 5m + 1h + cache
    // read (output excluded)"; A9/A13: only the exact claude-haiku-5-5 id and its 8-digit
    // date-snapshot form are tiered, never prefix ids.
    // ------------------------------------------------------------------

    [Fact]
    public void RateFor_Haiku55_IsTheNormalTier_BecauseTheHighTierExistsOnlyInsideCost()
    {
        // Table row "0.10 / **0.50** | 0.50 / **2.50** | 0.01 / **0.05**": the first values are the
        // normal tier; the handoff applies the second values "only through Cost".
        var rate = ClaudeModelPricing.RateFor("claude-haiku-5-5");
        Assert.Equal(0.10m, rate.InputPerMillion);
        Assert.Equal(0.50m, rate.OutputPerMillion);
        Assert.Equal(0.01m, rate.CacheReadPerMillion);
        Assert.Equal(0.125m, rate.CacheWrite5mPerMillion);   // A8: 0.10 × 1.25
        Assert.Equal(0.20m, rate.CacheWrite1hPerMillion);    // A8: 0.10 × 2.0
    }

    [Fact]
    public void Cost_Haiku55_PromptExactly100000_UsesNormalRates()
    {
        // "> 100,000" is not met at 100,000 (handoff: "tier at 100,000 (low)").
        // Hand calc: 100,000 × 0.10 / 1,000,000 = 0.01.
        Assert.Equal(0.01m, ClaudeModelPricing.Cost("claude-haiku-5-5", inputTokens: 100_000));
    }

    [Fact]
    public void Cost_Haiku55_Prompt100001_UsesHigherRates()
    {
        // Handoff: "100,001 (high)". Hand calc: 100,001 × 0.50 / 1,000,000 = 0.0500005.
        Assert.Equal(0.0500005m, ClaudeModelPricing.Cost("claude-haiku-5-5", inputTokens: 100_001));
    }

    [Fact]
    public void Cost_Haiku55_PromptIsInputPlus5mPlus1hPlusCacheRead_StaysNormalAtExactly100000()
    {
        // Handoff: "prompt = input + 5m + 1h + cache read (output excluded)".
        // Prompt: 40,000 + 20,000 + 20,000 + 20,000 = 100,000 → normal tier.
        // Hand calc: (40,000×0.10 + 20,000×0.125 + 20,000×0.20 + 20,000×0.01) / 1M
        //          = (4,000 + 2,500 + 4,000 + 200) / 1M = 10,700 / 1M = 0.0107.
        var cost = ClaudeModelPricing.Cost(
            "claude-haiku-5-5",
            inputTokens: 40_000,
            cacheWrite5mTokens: 20_000,
            cacheWrite1hTokens: 20_000,
            cacheReadTokens: 20_000);
        Assert.Equal(0.0107m, cost);
    }

    [Fact]
    public void Cost_Haiku55_CacheWrite5mCountsIntoThePrompt_AndPricesAt125PercentOfTheHighInputRate()
    {
        // Prompt: 50,000 + 60,000 = 110,000 > 100,000 → high tier for all of the record's tokens.
        // Hand calc: (50,000×0.50 + 60,000×(0.50×1.25=0.625)) / 1M = (25,000 + 37,500) / 1M = 0.0625.
        // A 5m-excluded implementation would stay normal: (50,000×0.10 + 60,000×0.125) = 0.0125 ≠ 0.0625.
        var cost = ClaudeModelPricing.Cost("claude-haiku-5-5", inputTokens: 50_000, cacheWrite5mTokens: 60_000);
        Assert.Equal(0.0625m, cost);
    }

    [Fact]
    public void Cost_Haiku55_CacheWrite1hCountsIntoThePrompt_AndPricesAt200PercentOfTheHighInputRate()
    {
        // Prompt: 50,000 + 60,000 = 110,000 > 100,000 → high tier (A8: 1h = 0.50 × 2.0 = 1.00).
        // Hand calc: (50,000×0.50 + 60,000×1.00) / 1M = (25,000 + 60,000) / 1M = 0.085.
        // A 1h-excluded implementation would stay normal: (50,000×0.10 + 60,000×0.20) = 0.017 ≠ 0.085.
        var cost = ClaudeModelPricing.Cost("claude-haiku-5-5", inputTokens: 50_000, cacheWrite1hTokens: 60_000);
        Assert.Equal(0.085m, cost);
    }

    [Fact]
    public void Cost_Haiku55_CacheReadCountsIntoThePrompt_AndPricesAtTheHighCacheReadRate()
    {
        // Prompt: 10 + 100,001 = 100,011 > 100,000 → high tier.
        // Hand calc: (10×0.50 + 100,001×0.05) / 1M = (5 + 5,000.05) / 1M = 0.00500505.
        // A read-excluded implementation would stay normal: (10×0.10 + 100,001×0.01) = 0.00100101.
        var cost = ClaudeModelPricing.Cost("claude-haiku-5-5", inputTokens: 10, cacheReadTokens: 100_001);
        Assert.Equal(0.00500505m, cost);
    }

    [Fact]
    public void Cost_Haiku55_OutputTokensAreExcludedFromThePrompt()
    {
        // Handoff: "(output excluded)" — 1 input + 1,000,000 output → prompt 1 ≤ 100,000 → normal.
        // Hand calc: (1×0.10 + 1,000,000×0.50) / 1M = 500,000.10 / 1M = 0.5000001.
        // An output-counting implementation would flip to high: (1×0.50 + 1,000,000×2.50) = 2.5000005.
        var cost = ClaudeModelPricing.Cost("claude-haiku-5-5", inputTokens: 1, outputTokens: 1_000_000);
        Assert.Equal(0.5000001m, cost);
    }

    [Fact]
    public void Cost_Haiku55DatedSnapshot_IsAlsoTiered()
    {
        // A9/A13: "The >100k tier applies only to exact claude-haiku-5-5 (and its date-snapshot form)."
        // Same 100,001 case as the exact id: 100,001 × 0.50 / 1M = 0.0500005.
        Assert.Equal(0.0500005m, ClaudeModelPricing.Cost("claude-haiku-5-5-20261101", inputTokens: 100_001));
    }

    [Fact]
    public void Cost_Haiku55WithNonDigitTail_IsPrefixFallback_AndNeverTiers()
    {
        // Handoff: "exact id and 8-digit date form only, never prefix ids"; A9/A13: prefix
        // fallbacks have one tier only. "claude-haiku-5-5-preview" trims to nothing (tail is not
        // 8 digits), so it falls to the "claude-haiku" prefix → 0.10/0.50/0.01, Estimated.
        // Prompt 10 + 200,000 = 200,010, yet the normal prefix rates must price it:
        // (10×0.10 + 200,000×0.125) / 1M = (1 + 25,000) / 1M = 0.025001. A tiering implementation
        // would answer 0.125005 (10×0.50 + 200,000×0.625).
        Assert.Equal(PricingKind.Estimated, ClaudeModelPricing.PriceKind("claude-haiku-5-5-preview"));
        Assert.Equal(0.025001m,
            ClaudeModelPricing.Cost("claude-haiku-5-5-preview", inputTokens: 10, cacheWrite5mTokens: 200_000));
    }

    [Fact]
    public void Cost_Haiku55WithSevenDigitTail_IsPrefixFallback_AndNeverTiers()
    {
        // "exact id and 8-digit date form only": a 7-digit tail is not a date snapshot, so
        // "claude-haiku-5-5-2026110" resolves through the prefix at the single normal tier.
        // Hand calc: 100,001 × 0.10 / 1M = 0.0100001. A tiering implementation would answer 0.0500005.
        Assert.Equal(PricingKind.Estimated, ClaudeModelPricing.PriceKind("claude-haiku-5-5-2026110"));
        Assert.Equal(0.0100001m, ClaudeModelPricing.Cost("claude-haiku-5-5-2026110", inputTokens: 100_001));
    }

    [Fact]
    public void Cost_HaikuOtherFamilyIds_ArePrefixFallbacks_AndNeverTier()
    {
        // A9/A13: only exact claude-haiku-5-5 tiers. "claude-haiku-6" hits the haiku prefix
        // (0.10/0.50/0.01, one tier). Prompt 200,000 is irrelevant: 200,000 × 0.10 / 1M = 0.02.
        Assert.Equal(0.02m, ClaudeModelPricing.Cost("claude-haiku-6", inputTokens: 200_000));
    }

    [Fact]
    public void Cost_Haiku45_IsExactButNeverTiered()
    {
        // A9/A13: the tier belongs to claude-haiku-5-5 only. claude-haiku-4-5 table row 1/5/0.10.
        // Hand calc: (1,000,000×1.00 + 1,000,000×0.10) / 1M = 1.10 even at a 2,000,000-token prompt.
        Assert.Equal(1.10m, ClaudeModelPricing.Cost("claude-haiku-4-5", inputTokens: 1_000_000, cacheReadTokens: 1_000_000));
    }

    // ------------------------------------------------------------------
    // Spec oracles — §P2a: "Oracles for tests: opus-5-5, 1M input + 1M output + 1M cache read →
    // 4 + 20 + 0.20 = 24.20; fable-5-1, 2M cache read → 0.50; haiku-5-5 record with 60k input +
    // 50k cache read (110k) and 1k output → 0.06×0.50 + 0.05×0.05 + 0.001×2.50 = 0.03 + 0.0025 +
    // 0.0025 = 0.035."
    // ------------------------------------------------------------------

    [Fact]
    public void Cost_SpecOracle_Opus55_MillionTokensOfEach_Is24_20()
    {
        // Oracle 1. Hand calc: 1M×4 + 1M×20 + 1M×0.20 = 24,200,000 → /1M = 24.20.
        // The tier check does not apply (only haiku-5-5 tiers, A9/A13).
        var cost = ClaudeModelPricing.Cost(
            "claude-opus-5-5", inputTokens: 1_000_000, outputTokens: 1_000_000, cacheReadTokens: 1_000_000);
        Assert.Equal(24.20m, cost);
    }

    [Fact]
    public void Cost_SpecOracle_Fable51_2MCacheRead_Is0_50()
    {
        // Oracle 2. Hand calc: 2M × 0.25 = 500,000 → /1M = 0.50 (table row "**0.25** | was 1.00 (×0.1)").
        Assert.Equal(0.50m, ClaudeModelPricing.Cost("claude-fable-5-1", cacheReadTokens: 2_000_000));
    }

    [Fact]
    public void Cost_SpecOracle_Haiku55_110kPrompt_Is0_035()
    {
        // Oracle 3. Prompt = 60,000 + 50,000 = 110,000 > 100,000 → high tier; output excluded from
        // the prompt. Hand calc: 0.06M×0.50 + 0.05M×0.05 + 0.001M×2.50 = 0.03 + 0.0025 + 0.0025 = 0.035.
        var cost = ClaudeModelPricing.Cost(
            "claude-haiku-5-5", inputTokens: 60_000, outputTokens: 1_000, cacheReadTokens: 50_000);
        Assert.Equal(0.035m, cost);
    }
}
