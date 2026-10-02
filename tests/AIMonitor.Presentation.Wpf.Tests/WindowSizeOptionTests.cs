using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class WindowSizeOptionTests
{
    [Fact]
    public void PresetOptions_MatchPython133LabelsAndDimensions()
    {
        Assert.Equal("Compact · 960 × 680", WindowSizeOption.Compact.DisplayName);
        Assert.Equal(960, WindowSizeOption.Compact.Width);
        Assert.Equal(680, WindowSizeOption.Compact.Height);

        Assert.Equal("Standard · 1120 × 820 · default", WindowSizeOption.Standard.DisplayName);
        Assert.Equal(1120, WindowSizeOption.Standard.Width);
        Assert.Equal(820, WindowSizeOption.Standard.Height);

        Assert.Equal("Wide · 1400 × 900", WindowSizeOption.Wide.DisplayName);
        Assert.Equal(1400, WindowSizeOption.Wide.Width);
        Assert.Equal(900, WindowSizeOption.Wide.Height);

        Assert.Equal("Remember last size", WindowSizeOption.RememberLastSize.DisplayName);
        Assert.Equal(0, WindowSizeOption.RememberLastSize.Width);
        Assert.Equal(0, WindowSizeOption.RememberLastSize.Height);

        Assert.Equal(4, WindowSizeOption.All.Count);
        Assert.Same(WindowSizeOption.Compact, WindowSizeOption.All[0]);
        Assert.Same(WindowSizeOption.Standard, WindowSizeOption.All[1]);
        Assert.Same(WindowSizeOption.Wide, WindowSizeOption.All[2]);
        Assert.Same(WindowSizeOption.RememberLastSize, WindowSizeOption.All[3]);
    }

    [Theory]
    [InlineData(960, 680, "Compact · 960 × 680")]
    [InlineData(1120, 820, "Standard · 1120 × 820 · default")]
    [InlineData(1400, 900, "Wide · 1400 × 900")]
    [InlineData(0, 0, "Remember last size")]
    [InlineData(800, 600, "Standard · 1120 × 820 · default")] // Non-preset falls back to Standard
    [InlineData(1920, 1080, "Standard · 1120 × 820 · default")] // Non-preset falls back to Standard
    [InlineData(-1, -1, "Standard · 1120 × 820 · default")] // Non-preset falls back to Standard
    public void FromSize_ReturnsMatchingPresetOrFallsBackToStandard(int width, int height, string expectedName)
    {
        var option = WindowSizeOption.FromSize(width, height);
        Assert.Equal(expectedName, option.DisplayName);

        var fromDimensions = WindowSizeOption.FromDimensions(width, height);
        Assert.Same(option, fromDimensions);
    }

    [Fact]
    public void LabelAndToString_ReturnDisplayName()
    {
        var standard = WindowSizeOption.Standard;
        Assert.Equal(standard.DisplayName, standard.Label);
        Assert.Equal(standard.DisplayName, standard.ToString());
    }
}
