using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;

namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>
/// Draws the tray tile of 1.3.3 <c>TrayController._draw</c>: a rounded tile filled from the bottom to the
/// percentage in the severity colour, with the number across it. The digits are drawn twice - ink above the
/// fill line, white below it - because the line usually crosses them. Drawn large and scaled down so the
/// number stays crisp at whatever size the shell asks for.
/// </summary>
internal static class TrayIconRenderer
{
    private const int Canvas = 64;

    public static Bitmap Render(double? percent, Severity severity, ThemePalette palette, int size)
    {
        using var large = new Bitmap(Canvas, Canvas);
        using (var g = Graphics.FromImage(large))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var box = new RectangleF(2, 2, Canvas - 4, Canvas - 4);
            var radius = Canvas * 0.22f;
            using var tile = RoundedRect(box, radius);

            using (var track = new SolidBrush(ToDrawing(palette.Track)))
            {
                g.FillPath(track, tile);
            }

            var fraction = percent is null ? 0f : (float)(Math.Clamp(percent.Value, 0, 100) / 100.0);
            var split = box.Bottom - box.Height * fraction;
            if (fraction > 0)
            {
                var state = g.Save();
                g.SetClip(new RectangleF(box.Left, split, box.Width, box.Bottom - split));
                using var fill = new SolidBrush(ToDrawing(ThemePalette.StatusFor(severity)));
                g.FillPath(fill, tile);
                g.Restore(state);
            }

            var text = percent is null ? "—" : $"{percent.Value:F0}";
            using var font = new Font("Segoe UI", Canvas * (text.Length < 3 ? 0.46f : 0.36f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            var above = g.Save();
            g.SetClip(new RectangleF(box.Left, box.Top, box.Width, Math.Max(0, split - box.Top)));
            using (var ink = new SolidBrush(ToDrawing(palette.Ink)))
            {
                g.DrawString(text, font, ink, box, format);
            }

            g.Restore(above);

            if (fraction > 0)
            {
                var below = g.Save();
                g.SetClip(new RectangleF(box.Left, split, box.Width, box.Bottom - split));
                g.DrawString(text, font, Brushes.White, box, format);
                g.Restore(below);
            }
        }

        var scaled = new Bitmap(size, size);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(large, 0, 0, size, size);
        }

        return scaled;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ToDrawing(System.Windows.Media.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
