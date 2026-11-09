using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

/// <summary>
/// Tests for <see cref="TokenCounts"/> per SPEC R-014 §P1.1. Every numeric expectation cites the
/// spec line or shows the hand calculation (§5.6 item 2).
/// </summary>
public class AgentUsageTokenCountsTests
{
    [Fact]
    public void Other_SpecOracle_Total1000Buckets950_Is50()
    {
        // SPEC P1.4 oracle: "total 1,000 with buckets 100+200+300+350 = 950 → Other 50".
        // Hand calc: 100 + 200 + 300 + 350 = 950; 1000 − 950 = 50.
        var tokens = new TokenCounts(100, 200, 300, 350, 1000);

        Assert.Equal(50, tokens.Other);
    }

    [Fact]
    public void Other_ObservedAntigravityExample_Is314637()
    {
        // SPEC P1.1 observed example: "total 122,410,471 vs bucket sum 122,095,834 → Other 314,637".
        // Bucket split chosen to sum exactly: 50,000,000 + 40,000,000 + 20,000,000 + 12,095,834 = 122,095,834.
        // Hand calc: 122,410,471 − 122,095,834 = 314,637.
        var tokens = new TokenCounts(50_000_000, 40_000_000, 20_000_000, 12_095_834, 122_410_471);

        Assert.Equal(314_637, tokens.Other);
    }

    [Fact]
    public void Other_IsZero_WhenTotalEqualsBucketSum()
    {
        // SPEC P1.1: Other => Total − (Input + Output + CacheCreation + CacheRead); equality is allowed
        // (Total ≥ bucket sum). 100 + 200 + 300 + 350 = 950 = Total → Other = 0.
        var tokens = new TokenCounts(100, 200, 300, 350, 950);

        Assert.Equal(0, tokens.Other);
    }

    [Fact]
    public void Ctor_PreservesEveryFieldValue()
    {
        var tokens = new TokenCounts(11, 22, 33, 44, 110);

        Assert.Equal(11, tokens.Input);
        Assert.Equal(22, tokens.Output);
        Assert.Equal(33, tokens.CacheCreation);
        Assert.Equal(44, tokens.CacheRead);
        Assert.Equal(110, tokens.Total);
        // Hand calc: 11 + 22 + 33 + 44 = 110 → Other = 110 − 110 = 0.
        Assert.Equal(0, tokens.Other);
    }

    [Fact]
    public void Ctor_AllZeros_IsValid()
    {
        // SPEC P1.1 allows every value ≥ 0; 0 ≥ 0 and 0 ≥ 0 + 0 + 0 + 0.
        var tokens = new TokenCounts(0, 0, 0, 0, 0);

        Assert.Equal(0, tokens.Other);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, 0)]      // Input < 0
    [InlineData(0, -1, 0, 0, 0)]      // Output < 0
    [InlineData(0, 0, -1, 0, 0)]      // CacheCreation < 0
    [InlineData(0, 0, 0, -1, 0)]      // CacheRead < 0
    [InlineData(0, 0, 0, 0, -1)]      // Total < 0
    public void Ctor_NegativeValue_ThrowsArgumentOutOfRange(long input, long output, long cacheCreation, long cacheRead, long total)
    {
        // SPEC P1.1: "every value ≥ 0 … (else ArgumentOutOfRangeException)".
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenCounts(input, output, cacheCreation, cacheRead, total));
    }

    [Fact]
    public void Ctor_TotalBelowBucketSum_ThrowsArgumentOutOfRange()
    {
        // SPEC P1.1: "Total ≥ Input + Output + CacheCreation + CacheRead (else ArgumentOutOfRangeException)".
        // Hand calc: buckets 100 + 200 + 300 + 350 = 950; Total 949 < 950 → rejected.
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenCounts(100, 200, 300, 350, 949));
    }
}
