using System.Windows;
using AIMonitor.Presentation.Wpf.Controls;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class GaugeControlTests
{
    [Fact]
    public void DefaultValues_PreserveExistingBehaviour()
    {
        WpfTestHost.Run(() =>
        {
            var gauge = new GaugeControl();

            Assert.True(gauge.ShowSubtitle);
            Assert.True(gauge.ShowInlineValue);
            Assert.Equal(0.0, gauge.Value);
            Assert.Equal("0%", gauge.ValueText);
            Assert.Equal(string.Empty, gauge.Title);
            Assert.Equal(string.Empty, gauge.Subtitle);
        });
    }

    [Fact]
    public void ShowSubtitleAndShowInlineValue_CanBeSetAndRetrieved()
    {
        WpfTestHost.Run(() =>
        {
            var gauge = new GaugeControl
            {
                ShowSubtitle = false,
                ShowInlineValue = false
            };

            Assert.False(gauge.ShowSubtitle);
            Assert.False(gauge.ShowInlineValue);

            gauge.ShowSubtitle = true;
            gauge.ShowInlineValue = true;

            Assert.True(gauge.ShowSubtitle);
            Assert.True(gauge.ShowInlineValue);
        });
    }

    [Fact]
    public void FormatCaptionText_InlineValueTrue_ReturnsTitleOnly()
    {
        var result = GaugeControl.FormatCaptionText("Session", "45%", showInlineValue: true);
        Assert.Equal("Session", result);
    }

    [Fact]
    public void FormatCaptionText_InlineValueFalse_AppendsPercentTextToTitle()
    {
        // When inline value is false, percent moves into caption
        var resultWithTitle = GaugeControl.FormatCaptionText("Session", "45%", showInlineValue: false);
        Assert.Equal("Session 45%", resultWithTitle);

        var resultWithoutTitle = GaugeControl.FormatCaptionText("", "45%", showInlineValue: false);
        Assert.Equal("45%", resultWithoutTitle);

        var resultEmptyValue = GaugeControl.FormatCaptionText("Session", "", showInlineValue: false);
        Assert.Equal("Session", resultEmptyValue);
    }

    [Fact]
    public void MeasureOverride_WhenWidthAndHeightExplicitlySet_ReturnsDesiredSizeMatchingDimensions()
    {
        WpfTestHost.Run(() =>
        {
            var gauge = new GaugeControl
            {
                Width = 64.0,
                Height = 64.0
            };

            gauge.Measure(new Size(200, 200));

            Assert.Equal(64.0, gauge.DesiredSize.Width);
            Assert.Equal(64.0, gauge.DesiredSize.Height);
        });
    }
}
