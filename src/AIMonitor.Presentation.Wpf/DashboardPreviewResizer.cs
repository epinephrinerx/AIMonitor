namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// Handles live preview resizing of the dashboard window.
/// Remembers the window's dimensions on the first non-zero resize and restores
/// them when size is reverted to (0, 0) ("Remember last size").
/// </summary>
public sealed class DashboardPreviewResizer
{
    private readonly Func<System.Windows.Window?> _getWindow;
    private (double Width, double Height)? _rememberedSize;

    public DashboardPreviewResizer(Func<System.Windows.Window?> getWindow)
    {
        _getWindow = getWindow ?? throw new ArgumentNullException(nameof(getWindow));
    }

    public void Resize(int width, int height)
    {
        var window = _getWindow();
        if (window is null || !window.IsVisible || window.WindowState != System.Windows.WindowState.Normal)
        {
            return;
        }

        if (width != 0 || height != 0)
        {
            if (_rememberedSize is null)
            {
                _rememberedSize = (window.Width, window.Height);
            }

            window.Width = width;
            window.Height = height;
        }
        else
        {
            if (_rememberedSize is not null)
            {
                window.Width = _rememberedSize.Value.Width;
                window.Height = _rememberedSize.Value.Height;
                _rememberedSize = null;
            }
        }
    }
}
