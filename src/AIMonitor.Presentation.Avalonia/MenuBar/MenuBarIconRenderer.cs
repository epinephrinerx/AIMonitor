using AIMonitor.Domain;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AIMonitor.Presentation.Avalonia.MenuBar;

/// <summary>
/// Draws the menu-bar tile: a rounded square filled from the bottom to the percentage in the
/// severity colour, with the number across it. The digits are drawn twice — once above the fill line
/// and once below it in white — because the line usually crosses them, which is the same trick the
/// Windows tray tile uses.
///
/// Not a template image: a macOS template icon is flattened to a single colour, which would throw
/// away the severity the tile exists to show. The number is always there as well, so the colour is
/// never the only cue.
/// </summary>
internal static class MenuBarIconRenderer
{
    /// <summary>Drawn at 2x for a Retina menu bar, which is 22 points tall.</summary>
    private const int Points = 22;
    private const double Scale = 2;

    public static WindowIcon Render(double? percent, Severity severity, IBrush track, IBrush fill, IBrush ink)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(ink);

        var pixels = (int)(Points * Scale);
        var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels), new Vector(96 * Scale, 96 * Scale));
        using (var context = bitmap.CreateDrawingContext())
        {
            var box = new Rect(1, 1, Points - 2, Points - 2);
            var corner = Points * 0.22;
            var tile = new RoundedRect(box, corner);

            context.DrawRectangle(track, null, tile);

            var fraction = percent is null ? 0 : Math.Clamp(percent.Value, 0, 100) / 100.0;
            var split = box.Bottom - (box.Height * fraction);
            if (fraction > 0)
            {
                using (context.PushClip(new Rect(box.Left, split, box.Width, box.Bottom - split)))
                {
                    context.DrawRectangle(fill, null, tile);
                }
            }

            var text = percent is null ? "—" : $"{percent.Value:F0}";
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                // No font is named: macOS has no Segoe UI, and asking for a font that is not
                // installed makes the toolkit substitute one glyph run at a time.
                Typeface.Default,
                Points * (text.Length < 3 ? 0.62 : 0.5),
                ink);

            var origin = new Point(
                box.Left + ((box.Width - formatted.Width) / 2),
                box.Top + ((box.Height - formatted.Height) / 2));

            using (context.PushClip(new Rect(box.Left, box.Top, box.Width, Math.Max(0, split - box.Top))))
            {
                context.DrawText(formatted, origin);
            }

            if (fraction > 0)
            {
                formatted.SetForegroundBrush(Brushes.White);
                using (context.PushClip(new Rect(box.Left, split, box.Width, box.Bottom - split)))
                {
                    context.DrawText(formatted, origin);
                }
            }
        }

        return new WindowIcon(bitmap);
    }
}
