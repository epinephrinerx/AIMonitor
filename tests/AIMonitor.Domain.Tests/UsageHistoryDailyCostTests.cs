using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

/// <summary>
/// R-017 G1.1 + F6 — <c>DailyCost</c> and the optional <c>UsageHistory.DailyCosts</c>.
/// Written from Docs/SPEC_R017.md only (§5.6): G1.1 fixes the record shape
/// (<c>DailyCost(DateOnly Day, double PricedUsd, bool HasUnpricedModels)</c>), the optional last
/// constructor parameter <c>dailyCosts = null</c> and the property that is empty when null; F6
/// (the Coding Owner's decision after Inspector A) pins the guard: <c>PricedUsd</c> must be finite
/// and >= 0 — NaN, ±Infinity or negative throw <c>ArgumentOutOfRangeException</c>, "reuse the
/// existing guard" (the bucket-value guard in <see cref="UsageHistoryBucket"/>). The implementation
/// is written in parallel; these tests compile only after it merges.
/// </summary>
public sealed class UsageHistoryDailyCostTests
{
    private static readonly DateOnly Day = new(2026, 11, 8);

    // ── G1.1: the record itself ────────────────────────────────────────────────

    [Fact]
    public void DailyCost_CarriesDayPricedUsdAndUnpricedFlag()
    {
        var cost = new DailyCost(Day, 0.60, true);

        Assert.Equal(Day, cost.Day);
        Assert.Equal(0.60, cost.PricedUsd, 10);
        Assert.True(cost.HasUnpricedModels);
    }

    [Fact]
    public void DailyCost_ZeroPricedUsd_IsAccepted()
    {
        // F6: the guard is finite AND >= 0 — zero passes (the empty-window days carry 0 per G1.2/H4).
        var cost = new DailyCost(Day, 0.0, false);

        Assert.Equal(0.0, cost.PricedUsd);
        Assert.False(cost.HasUnpricedModels);
    }

    [Theory]
    // F6 (m3): the same guard as bucket values (RequireFiniteNonNegative, pinned by the identical
    // case list in UsageHistoryTests) — NaN, ±Infinity and negative all rejected.
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(-1.0)]
    public void DailyCost_RejectsNonFiniteOrNegativePricedUsd(double pricedUsd)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DailyCost(Day, pricedUsd, false));
    }

    // ── G1.1: UsageHistory gains the optional last parameter ───────────────────

    [Fact]
    public void UsageHistory_DailyCosts_DefaultsToEmptyWhenParameterOmitted()
    {
        // G1.1: "optional constructor parameter dailyCosts = null (last parameter)"; the property
        // is "empty when null", never null.
        var history = new UsageHistory();

        Assert.Empty(history.DailyCosts);
    }

    [Fact]
    public void UsageHistory_DailyCosts_Null_GivesEmpty()
    {
        var history = new UsageHistory(dailyCosts: null);

        Assert.Empty(history.DailyCosts);
    }

    [Fact]
    public void UsageHistory_DailyCosts_CarriesTheGivenEntriesInOrder()
    {
        var costs = new[] { new DailyCost(Day, 0.60, false), new DailyCost(Day.AddDays(1), 1.25, true) };

        var history = new UsageHistory(dailyCosts: costs);

        Assert.Equal(costs, history.DailyCosts);
    }

    [Fact]
    public void UsageHistory_DailyCosts_MutatingTheSourceListAfterConstruction_DoesNotAlterTheHistory()
    {
        // Same guarantee the sibling lists carry (Buckets/Series/ByModel in UsageHistoryTests).
        var costs = new List<DailyCost> { new(Day, 0.60, false) };
        var history = new UsageHistory(dailyCosts: costs);

        costs.Clear();

        Assert.Single(history.DailyCosts);
    }

    [Fact]
    public void UsageHistory_DailyCosts_ExposedPropertyIsGenuinelyReadOnly()
    {
        var history = new UsageHistory(dailyCosts: [new DailyCost(Day, 0.60, false)]);

        var asList = Assert.IsAssignableFrom<IList<DailyCost>>(history.DailyCosts);

        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList.Add(new DailyCost(Day.AddDays(1), 1.0, false)));
        Assert.Single(history.DailyCosts);
    }

    [Fact]
    public void UsageHistory_ExistingPositionalCallers_StillWorkAndDailyCostsIsEmpty()
    {
        // G1.1: "existing callers are unchanged" — the six leading parameters keep their positions,
        // so the old all-positional call shape must still compile and behave.
        var history = new UsageHistory(
            [new UsageHistoryBucket(Day, new Dictionary<string, double> { ["opus"] = 3.0 })],
            ["opus"],
            [new UsageHistoryBreakdown("opus", 3.0)],
            [],
            14,
            "Total tokens");

        Assert.Single(history.Buckets);
        Assert.Empty(history.DailyCosts);
    }
}
