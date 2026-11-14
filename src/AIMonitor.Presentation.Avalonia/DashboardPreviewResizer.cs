using Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia;

/// <summary>
/// Resizes the dashboard while a window size is being previewed in Settings, as on Windows. The
/// size the window had before the first preview is remembered and put back when the preview
/// returns to "Remember last size" (0 × 0).
/// </summary>
public sealed class DashboardPreviewResizer(Func<Window?> getWindow)
{
    private readonly Func<Window?> _getWindow = getWindow ?? throw new ArgumentNullException(nameof(getWindow));
    private (double Width, double Height)? _rememberedSize;

    public void Resize(int width, int height)
    {
        var window = _getWindow();
        if (window is null || !window.IsVisible || window.WindowState != WindowState.Normal)
        {
            return;
        }

        if (width != 0 || height != 0)
        {
            _rememberedSize ??= (window.Width, window.Height);
            window.Width = width;
            window.Height = height;
        }
        else if (_rememberedSize is { } remembered)
        {
            window.Width = remembered.Width;
            window.Height = remembered.Height;
            _rememberedSize = null;
        }
    }
}
