using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMonitor.Presentation.Wpf.Controls;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-018 G1/G2/G3 (render half) — the dashboard (non-compact) GaugeControl path must keep the
/// caption and subtitle inside the card: G1 lays the caption out with MaxTextWidth = ActualWidth − 8
/// (≥ 1), Center, MaxLineCount 2, CharacterEllipsis at x = 4; G2 the same for the subtitle at
/// MaxLineCount 1; G3/D8 keeps a caption that fits on one line at exactly the current point
/// (center.X − width/2, center.Y + radius*0.45). Written from Docs/SPEC_R018.md; the decision matrix
/// itself is pinned in <see cref="GaugeControlDashboardTextTests"/> — these pixel tests prove the
/// wiring: no ink paints into the neighbouring card (base bug: long AGY captions overflowed the card
/// at 1120 px), the text is still drawn (never hidden), and an one-line caption is byte-stable and
/// centred. Geometry for the 200×200 card: center (100, 96), radius 76, outer ring edge ≈ 182.1,
/// captionTop ≈ 130.2, so ink at x ∈ [184..197) can only be caption/subtitle text, never the arc.
/// These fail at the base commit (untrimmed FormattedText) and are the expected-red render half of
/// R-018; the "renders exactly as before" test passes at the base and must keep passing.
/// </summary>
[Collection("Wpf")]
public sealed class GaugeControlCaptionFitTests
{
    // 200×200 card rendered into a 240-wide bitmap: columns 200..239 are OUTSIDE the card — ink
    // there is bleeding into the neighbour (the reported defect).
    private const int Card = 200;
    private const int BitmapWidth = 240;
    private const int Stride = BitmapWidth * 4;

    // Ink is sampled from x = 203: text trimmed to the [4 .. 196] block plus glyph/anti-alias
    // tolerance reaches at most ~198–200, so ink from 203 on is unambiguous card-escape, and a
    // long untrimmed caption (~600 px wide at the base) always reaches it.
    private const int OutsideFrom = 203;

    private const string LongText =
        "Gemini / GPT shared weekly pool usage window that is far wider than two hundred pixels at this font size";

    private static byte[] RenderGauge(string title, string subtitle)
    {
        var gauge = new GaugeControl
        {
            Width = Card,
            Height = Card,
            CaptionLines = 0, // dashboard path (RenderCompact is unchanged and stays pinned elsewhere)
            Value = 50.0,
            ValueText = "50%",
            ShowInlineValue = true,
            Title = title,
            Subtitle = subtitle,
        };

        gauge.Measure(new Size(Card, Card));
        gauge.Arrange(new Rect(0, 0, Card, Card));
        gauge.UpdateLayout();

        var rtb = new RenderTargetBitmap(BitmapWidth, Card, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(gauge);

        var pixels = new byte[Stride * Card];
        rtb.CopyPixels(pixels, Stride, 0);
        return pixels;
    }

    private static bool HasInk(byte[] pixels, int xFrom, int xTo, int yFrom, int yTo)
    {
        for (var y = yFrom; y < Math.Min(yTo, Card); y++)
        {
            for (var x = xFrom; x < Math.Min(xTo, BitmapWidth); x++)
            {
                if (pixels[(y * Stride) + (x * 4) + 3] > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void Render_DashboardLongCaption_InkStaysInsideCard_AndCaptionIsStillDrawn()
    {
        WpfTestHost.Run(() =>
        {
            var pixels = RenderGauge(LongText, subtitle: string.Empty);

            // G1: nothing paints into the neighbouring card (x ≥ 203; the 200-wide card plus the
            // 4 px inset margin can legitimately ink to ~198–200). The arc's outer ink reaches
            // ≈ 182, so the base's overflow here can only be the untrimmed caption.
            Assert.False(HasInk(pixels, OutsideFrom, BitmapWidth, 0, Card));

            // ...and the caption is trimmed to the card, not hidden: ink fills the right end of the
            // [4 .. 196] text block (x 184..196, below the value text and inside the caption band).
            Assert.True(HasInk(pixels, 184, 197, 131, 200));
        });
    }

    [Fact]
    public void Render_DashboardLongSubtitle_InkStaysInsideCard_AndSubtitleIsStillDrawn()
    {
        WpfTestHost.Run(() =>
        {
            var pixels = RenderGauge("Session", LongText);

            // G2: the subtitle (MaxTextWidth = ActualWidth − 8, one line, CharacterEllipsis) must
            // not bleed out of the card either.
            Assert.False(HasInk(pixels, OutsideFrom, BitmapWidth, 0, Card));

            // Subtitle band: top ≈ height − subtitleHeight − 4 ≈ 177.8; its ellipsis reaches the
            // right end of the text block (past the ring's 182.1 outer edge).
            Assert.True(HasInk(pixels, 184, 197, 178, 200));
        });
    }

    [Fact]
    public void Render_DashboardOneLineCaption_UnaffectedBySubtitle_AndCentred()
    {
        WpfTestHost.Run(() =>
        {
            var withoutSubtitle = RenderGauge("Session", string.Empty);
            var withSubtitle = RenderGauge("Session", "Resets in 2h");

            // D8: "A caption that fits on one line draws at exactly the current point ... keep the
            // old drawing call for that case". The window below isolates the one caption line: rows
            // 133..149 (caption block top is 130.2, cap ink starts a few px lower; the value text
            // ends ≈ 119 above and the subtitle ink starts ≈ 180 below) and columns 56..142 — the
            // ring's inner edge reaches x ≥ 145.7 / ≤ 54.3 only from row 149 on, so the window
            // holds caption ink alone. With the caption fitting on one line, the subtitle's
            // presence must not move or resize it: byte-identical in the window.
            const int RowTop = 133, RowBottom = 150, ColLeft = 56, ColRight = 143;
            for (var y = RowTop; y < RowBottom; y++)
            {
                var start = (y * Stride) + ColLeft * 4;
                var length = (ColRight - ColLeft) * 4;
                Assert.True(withoutSubtitle.AsSpan(start, length)
                    .SequenceEqual(withSubtitle.AsSpan(start, length)),
                    $"caption band row {y} changed when a subtitle was drawn");
            }

            // The caption ink is symmetric about the card centre x = 100 — the old manual centring
            // and the new x = 4 + TextAlignment.Center agree for one line — and it starts just
            // below captionTop (130.2 + the ascent gap of an ~18.2 px bold em).
            int left = -1, right = -1, top = -1;
            for (var y = RowTop; y < RowBottom; y++)
            {
                for (var x = ColLeft; x < ColRight; x++)
                {
                    if (withoutSubtitle[(y * Stride) + (x * 4) + 3] > 0)
                    {
                        if (top < 0) top = y;
                        if (x < left || left < 0) left = x;
                        if (x > right) right = x;
                    }
                }
            }

            Assert.True(left >= 0, "the caption rendered no ink in its band");
            Assert.InRange(top, RowTop, 143);
            Assert.True(right < Card, "the caption bled out of the card");
            Assert.True(Math.Abs((Card - 1 - right) - left) <= 2,
                $"caption not centred: left margin {left}, right margin {Card - 1 - right}");
        });
    }
}
