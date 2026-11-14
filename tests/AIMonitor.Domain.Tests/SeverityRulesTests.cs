using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class SeverityRulesTests
{
    [Theory]
    [InlineData(0.0, Severity.Normal)]
    [InlineData(74.9, Severity.Normal)]
    [InlineData(75.0, Severity.High)]
    [InlineData(89.9, Severity.High)]
    [InlineData(90.0, Severity.Critical)]
    [InlineData(100.0, Severity.Critical)]
    public void LocalSeverity_AppliesThresholdBoundaries(double percent, Severity expected)
    {
        Assert.Equal(expected, SeverityRules.LocalSeverity(percent));
    }

    [Fact]
    public void LocalSeverity_WhenPercentIsMissing_IsNormal()
    {
        Assert.Equal(Severity.Normal, SeverityRules.LocalSeverity(null));
    }

    [Theory]
    [InlineData(74.9, Severity.Normal)]
    [InlineData(75.0, Severity.High)]
    [InlineData(89.9, Severity.High)]
    [InlineData(90.0, Severity.Critical)]
    public void Combine_WithNormalServerSeverity_UsesLocalThreshold(double percent, Severity expected)
    {
        Assert.Equal(expected, SeverityRules.Combine(percent, Severity.Normal));
    }

    [Fact]
    public void Combine_WhenServerSeverityIsWorseThanLocal_KeepsServerSeverity()
    {
        Assert.Equal(Severity.Critical, SeverityRules.Combine(5.0, Severity.Critical));
        Assert.Equal(Severity.VeryHigh, SeverityRules.Combine(0.0, Severity.VeryHigh));
    }

    [Fact]
    public void Combine_WhenLocalIsWorseThanServer_UsesLocal()
    {
        Assert.Equal(Severity.Critical, SeverityRules.Combine(95.0, Severity.Normal));
    }

    [Fact]
    public void Combine_WhenPercentIsMissing_PreservesServerSeverity()
    {
        Assert.Equal(Severity.High, SeverityRules.Combine(null, Severity.High));
        Assert.Equal(Severity.Normal, SeverityRules.Combine(null, Severity.Normal));
    }

    [Fact]
    public void Combine_RejectsAnUndefinedServerSeverity_RatherThanLettingItOutrankCritical()
    {
        var invalid = (Severity)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => SeverityRules.Combine(0.0, invalid));
    }

    [Fact]
    public void Parse_UnknownLabel_DoesNotOutrankAKnownSeverity()
    {
        var unknown = SeverityLabels.Parse("mystery-status");

        Assert.Equal(Severity.Normal, unknown);
        Assert.Equal(Severity.Critical, SeverityRules.Combine(95.0, unknown));
        Assert.Equal(Severity.Normal, SeverityRules.Combine(10.0, unknown));
    }

    [Theory]
    [InlineData(null, Severity.Normal)]
    [InlineData("", Severity.Normal)]
    [InlineData("normal", Severity.Normal)]
    [InlineData("warning", Severity.High)]
    [InlineData("HIGH", Severity.High)]
    [InlineData("serious", Severity.VeryHigh)]
    [InlineData("very high", Severity.VeryHigh)]
    [InlineData("critical", Severity.Critical)]
    [InlineData("CRITICAL", Severity.Critical)]
    public void Parse_MapsKnownLabelsCaseInsensitively(string? label, Severity expected)
    {
        Assert.Equal(expected, SeverityLabels.Parse(label));
    }

    [Theory]
    [InlineData(Severity.Normal, "✓", "Normal")]
    [InlineData(Severity.High, "⚠", "High")]
    [InlineData(Severity.VeryHigh, "⚠", "Very high")]
    [InlineData(Severity.Critical, "⚠", "Critical")]
    public void Describe_ReturnsGlyphAndLabel(Severity severity, string glyph, string label)
    {
        var (actualGlyph, actualLabel) = SeverityLabels.Describe(severity);

        Assert.Equal(glyph, actualGlyph);
        Assert.Equal(label, actualLabel);
    }
}
