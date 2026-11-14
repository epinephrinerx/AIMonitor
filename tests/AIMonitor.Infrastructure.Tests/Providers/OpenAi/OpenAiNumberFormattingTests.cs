using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

public sealed class OpenAiNumberFormattingTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1284, "1,284")]
    [InlineData(9999, "9,999")]
    [InlineData(12900, "12.9K")]
    [InlineData(10000, "10K")]
    [InlineData(4200000, "4.2M")]
    [InlineData(1000000, "1M")]
    [InlineData(2500000000, "2.5B")]
    public void FormatCompactNumber_MatchesThePythonBaselineFormat(double value, string expected) =>
        Assert.Equal(expected, OpenAiNumberFormatting.FormatCompactNumber(value));

    [Fact]
    public void FormatMoney_ZeroIsExactlyZeroNotBelowOneCent()
    {
        Assert.Equal("$0.00", OpenAiNumberFormatting.FormatMoney(0));
    }

    [Fact]
    public void FormatMoney_NonZeroBelowOneCentShowsPlaceholder()
    {
        Assert.Equal("<$0.01", OpenAiNumberFormatting.FormatMoney(0.001));
    }

    [Theory]
    [InlineData(5, "$5.00")]
    [InlineData(123.4, "$123.40")]
    [InlineData(999.99, "$999.99")]
    public void FormatMoney_BelowOneThousand_ShowsCents(double value, string expected) =>
        Assert.Equal(expected, OpenAiNumberFormatting.FormatMoney(value));

    [Fact]
    public void FormatMoney_AtOrAboveOneThousandButBelowTenThousand_ShowsGroupedWholeDollarsNotASuffix()
    {
        // money() routes >=1000 through compact(), whose K-suffix threshold is 10,000 - so this range
        // shows as a plain grouped integer, not "$1.5K".
        Assert.Equal("$1,500", OpenAiNumberFormatting.FormatMoney(1500));
    }

    [Fact]
    public void FormatMoney_AtOrAboveTenThousand_UsesCompactSuffixWithoutCents()
    {
        Assert.Equal("$12.9K", OpenAiNumberFormatting.FormatMoney(12900));
    }
}
