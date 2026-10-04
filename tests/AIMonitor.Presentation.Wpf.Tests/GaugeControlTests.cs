using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMonitor.Presentation.Wpf.Controls;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class GaugeControlTests
{
    private static byte[] RenderToPixels(FrameworkElement element, int width, int height)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        rtb.CopyPixels(pixels, stride, 0);
        return pixels;
    }

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
            Assert.Equal(0, gauge.CaptionLines);
            Assert.Equal(0.0, gauge.ArcSize);
            Assert.Equal(0.0, gauge.CaptionLineHeight);
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
    public void ComputeCompactLayout_Geometry_MatchesSpecification()
    {
        double width = 80.0;
        double arc = 65.0;
        int captionLines = 2;
        double lineHeight = 13.0;

        var layout = GaugeControl.ComputeCompactLayout(width, arc, captionLines, lineHeight);

        Assert.True(layout.Title.Top >= layout.Ring.Bottom);
        Assert.True(layout.Subtitle.Top >= layout.Title.Bottom);
        Assert.Equal(layout.Subtitle.Bottom, layout.Height);
        Assert.Equal(width / 2.0, layout.Ring.X + (layout.Ring.Width / 2.0));

        // When captionLines is 1, subtitle must be empty
        var singleLineLayout = GaugeControl.ComputeCompactLayout(width, arc, 1, lineHeight);
        Assert.Equal(Rect.Empty, singleLineLayout.Subtitle);
    }

    [Fact]
    public void MeasureOverride_CompactMode_ReturnsDesiredHeightMatchingCompactLayout()
    {
        WpfTestHost.Run(() =>
        {
            var gauge = new GaugeControl
            {
                CaptionLines = 2,
                ArcSize = 65.0,
                CaptionLineHeight = 13.0
            };

            gauge.Measure(new Size(80.0, 200.0));

            Assert.Equal(65.0 + 2.0 + 26.0, gauge.DesiredSize.Height);
        });
    }

    [Fact]
    public void Render_CompactMode_TitleAndSubtitleDoNotOverlap()
    {
        WpfTestHost.Run(() =>
        {
            int width = 80;
            int height = 93;
            int stride = width * 4;

            var gaugeTitleOnly = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session",
                Subtitle = string.Empty,
                ShowSubtitle = true,
                ShowInlineValue = true
            };

            var gaugeWithSubtitle = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session",
                Subtitle = "Resets in 2h",
                ShowSubtitle = true,
                ShowInlineValue = true
            };

            byte[] pixelsTitleOnly = RenderToPixels(gaugeTitleOnly, width, height);
            byte[] pixelsWithSubtitle = RenderToPixels(gaugeWithSubtitle, width, height);

            // Title band rows [67, 80): must be byte-identical in both renders
            int titleStart = 67 * stride;
            int titleEnd = 80 * stride;
            Assert.True(pixelsTitleOnly.AsSpan(titleStart, titleEnd - titleStart)
                .SequenceEqual(pixelsWithSubtitle.AsSpan(titleStart, titleEnd - titleStart)));

            // Subtitle band rows [80, 93): must contain non-transparent pixels only in the second
            int subStart = 80 * stride;
            int subEnd = 93 * stride;

            bool titleOnlyHasSubPixels = false;
            for (int i = subStart; i < subEnd; i += 4)
            {
                if (pixelsTitleOnly[i + 3] > 0)
                {
                    titleOnlyHasSubPixels = true;
                    break;
                }
            }

            bool withSubHasSubPixels = false;
            for (int i = subStart; i < subEnd; i += 4)
            {
                if (pixelsWithSubtitle[i + 3] > 0)
                {
                    withSubHasSubPixels = true;
                    break;
                }
            }

            Assert.False(titleOnlyHasSubPixels);
            Assert.True(withSubHasSubPixels);
        });
    }

    [Fact]
    public void Render_ShowSubtitleFalse_RendersIdenticallyToEmptySubtitle_AndDiffersWhenTrue()
    {
        WpfTestHost.Run(() =>
        {
            int width = 80;
            int height = 93;

            var gaugeShowSubtitleFalse = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session",
                Subtitle = "Resets in 2h",
                ShowSubtitle = false
            };

            var gaugeSubtitleEmpty = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session",
                Subtitle = string.Empty,
                ShowSubtitle = true
            };

            var gaugeShowSubtitleTrue = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session",
                Subtitle = "Resets in 2h",
                ShowSubtitle = true
            };

            byte[] pixelsFalse = RenderToPixels(gaugeShowSubtitleFalse, width, height);
            byte[] pixelsEmpty = RenderToPixels(gaugeSubtitleEmpty, width, height);
            byte[] pixelsTrue = RenderToPixels(gaugeShowSubtitleTrue, width, height);

            Assert.True(pixelsFalse.AsSpan().SequenceEqual(pixelsEmpty));
            Assert.False(pixelsTrue.AsSpan().SequenceEqual(pixelsEmpty));
        });
    }

    [Fact]
    public void Render_ShowInlineValue_AndFormatCaptionText_RenderAsSpecified()
    {
        WpfTestHost.Run(() =>
        {
            int width = 80;
            int height = 93;

            // 1. ShowInlineValue = false with Title "Session", ValueText "45%"
            var gauge1 = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session",
                ValueText = "45%",
                ShowInlineValue = false
            };

            // 2. ShowInlineValue = true with Title "Session 45%", ValueText ""
            var gauge2 = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session 45%",
                ValueText = string.Empty,
                ShowInlineValue = true
            };

            // 3. Title-only ShowInlineValue = true with ValueText "45%"
            var gauge3 = new GaugeControl
            {
                Width = width,
                Height = height,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "Session",
                ValueText = "45%",
                ShowInlineValue = true
            };

            byte[] pixels1 = RenderToPixels(gauge1, width, height);
            byte[] pixels2 = RenderToPixels(gauge2, width, height);
            byte[] pixels3 = RenderToPixels(gauge3, width, height);

            Assert.True(pixels1.AsSpan().SequenceEqual(pixels2));
            Assert.False(pixels1.AsSpan().SequenceEqual(pixels3));
        });
    }

    [Fact]
    public void Render_CompactMode_ElidesTitleToControlWidth()
    {
        WpfTestHost.Run(() =>
        {
            int controlWidth = 60;
            int controlHeight = 93;
            int bmpWidth = 200;
            int bmpHeight = 100;
            int stride = bmpWidth * 4;

            var gauge = new GaugeControl
            {
                Width = controlWidth,
                Height = controlHeight,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "An Extremely Long Title That Definitely Exceeds Sixty Pixels In Width By Far",
                ShowInlineValue = true
            };

            gauge.Measure(new Size(controlWidth, controlHeight));
            gauge.Arrange(new Rect(0, 0, controlWidth, controlHeight));
            gauge.UpdateLayout();

            var rtb = new RenderTargetBitmap(bmpWidth, bmpHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(gauge);

            byte[] pixels = new byte[stride * bmpHeight];
            rtb.CopyPixels(pixels, stride, 0);

            // Both assertions are needed:
            // 1. Negative assertion (x >= 61): verifies that ink does not bleed beyond the control width.
            // 2. Positive assertion (x >= 45): guards TextTrimming.CharacterEllipsis against wrap-and-drop.
            //    With MaxTextWidth=60, MaxLineCount=1, setting Trimming=None causes WPF FormattedText to
            //    word-wrap and drop subsequent lines, keeping only "An" centered (~22..37 DIP), leaving
            //    x >= 45 completely blank while still satisfying the negative assertion.
            //    Character ellipsis produces "An Extremely L..." spanning ~8..58 DIP, placing ink at x >= 45.
            bool hasPixelBeyondWidth = false;
            bool hasPixelAtOrBeyond45 = false;
            for (int y = 67; y < 80; y++)
            {
                for (int x = 45; x < bmpWidth; x++)
                {
                    int alphaIndex = (y * stride) + (x * 4) + 3;
                    if (pixels[alphaIndex] > 0)
                    {
                        hasPixelAtOrBeyond45 = true;
                        if (x >= 61)
                        {
                            hasPixelBeyondWidth = true;
                        }
                    }
                }
            }

            Assert.False(hasPixelBeyondWidth);
            Assert.True(hasPixelAtOrBeyond45);

            // Extra safety: render the same gauge with Title = "An" and assert the title bands are not byte-identical.
            // If wrap-and-drop occurred instead of ellipsis, both renders would produce identical pixels for "An".
            var gaugeShortTitle = new GaugeControl
            {
                Width = controlWidth,
                Height = controlHeight,
                ArcSize = 65.0,
                CaptionLines = 2,
                CaptionLineHeight = 13.0,
                Title = "An",
                ShowInlineValue = true
            };

            gaugeShortTitle.Measure(new Size(controlWidth, controlHeight));
            gaugeShortTitle.Arrange(new Rect(0, 0, controlWidth, controlHeight));
            gaugeShortTitle.UpdateLayout();

            var rtbShort = new RenderTargetBitmap(bmpWidth, bmpHeight, 96, 96, PixelFormats.Pbgra32);
            rtbShort.Render(gaugeShortTitle);

            byte[] shortPixels = new byte[stride * bmpHeight];
            rtbShort.CopyPixels(shortPixels, stride, 0);

            int titleStart = 67 * stride;
            int titleEnd = 80 * stride;
            Assert.False(pixels.AsSpan(titleStart, titleEnd - titleStart)
                .SequenceEqual(shortPixels.AsSpan(titleStart, titleEnd - titleStart)));
        });
    }

    [Fact]
    public void Render_DashboardMode_CaptionLinesZero_DrawsNonEmptyPixels()
    {
        WpfTestHost.Run(() =>
        {
            int size = 100;
            var gauge = new GaugeControl
            {
                Width = size,
                Height = size,
                CaptionLines = 0,
                Value = 50.0,
                ValueText = "50%",
                Title = "CPU",
                Subtitle = "Usage"
            };

            byte[] pixels = RenderToPixels(gauge, size, size);

            bool hasNonTransparent = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 0)
                {
                    hasNonTransparent = true;
                    break;
                }
            }

            Assert.True(hasNonTransparent);
        });
    }

    [Fact]
    public void ComputeCompactLayout_NegativeAndNanInputs_SanitisesAndDoesNotThrow()
    {
        // (a) width: -5, arc: -10, captionLines: 2, lineHeight: -3 does not throw and returns Height == 2 with non-negative rect sizes
        var layoutNegative = GaugeControl.ComputeCompactLayout(width: -5.0, arc: -10.0, captionLines: 2, lineHeight: -3.0);

        Assert.Equal(2.0, layoutNegative.Height);
        Assert.True(layoutNegative.Ring.Width >= 0.0);
        Assert.True(layoutNegative.Ring.Height >= 0.0);
        Assert.True(layoutNegative.Title.Width >= 0.0);
        Assert.True(layoutNegative.Title.Height >= 0.0);
        Assert.True(layoutNegative.Subtitle.Width >= 0.0);
        Assert.True(layoutNegative.Subtitle.Height >= 0.0);

        // NaN inputs
        var layoutNaN = GaugeControl.ComputeCompactLayout(width: double.NaN, arc: double.NaN, captionLines: 2, lineHeight: double.NaN);

        Assert.Equal(2.0, layoutNaN.Height);
        Assert.True(layoutNaN.Ring.Width >= 0.0);
        Assert.True(layoutNaN.Ring.Height >= 0.0);
        Assert.True(layoutNaN.Title.Width >= 0.0);
        Assert.True(layoutNaN.Title.Height >= 0.0);
        Assert.True(layoutNaN.Subtitle.Width >= 0.0);
        Assert.True(layoutNaN.Subtitle.Height >= 0.0);
    }

    [Fact]
    public void CompactMode_NegativeArcSize_MeasureAndRenderDoNotThrow()
    {
        WpfTestHost.Run(() =>
        {
            var gauge = new GaugeControl
            {
                CaptionLines = 2,
                ArcSize = -12.0,
                CaptionLineHeight = 13.0,
                Title = "Session",
                Subtitle = "Resets in 2h"
            };

            gauge.Measure(new Size(80.0, 200.0));

            Assert.True(double.IsFinite(gauge.DesiredSize.Height));
            Assert.True(gauge.DesiredSize.Height >= 0.0);

            gauge.Arrange(new Rect(0.0, 0.0, 80.0, 200.0));
            var rtb = new RenderTargetBitmap(80, 200, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(gauge);
        });
    }
}

