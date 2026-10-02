using System.Windows;
using AIMonitor.Presentation.Wpf;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class DashboardPreviewResizerTests
{
    [Fact]
    public void Resize_ToWide_ThenResizeZeroZero_RestoresOriginalSize()
    {
        WpfTestHost.Run(() =>
        {
            var window = new System.Windows.Window
            {
                Width = 1000,
                Height = 700,
                WindowState = System.Windows.WindowState.Normal
            };
            using var offscreen = WpfTestHost.ShowOffscreen(window);

            var resizer = new DashboardPreviewResizer(() => window);

            // 1. Resize to Wide (1400, 900)
            resizer.Resize(1400, 900);
            Assert.Equal(1400, window.Width);
            Assert.Equal(900, window.Height);

            // 2. Resize to (0, 0) ("Remember last size" on Discard) -> restores original (1000, 700)
            resizer.Resize(0, 0);
            Assert.Equal(1000, window.Width);
            Assert.Equal(700, window.Height);
        });
    }

    [Fact]
    public void Resize_WhenWindowNotVisible_DoesNotResize()
    {
        WpfTestHost.Run(() =>
        {
            var window = new System.Windows.Window
            {
                Width = 1000,
                Height = 700,
                WindowState = System.Windows.WindowState.Normal
            };
            // Window is NOT visible (Show() was never called)
            var resizer = new DashboardPreviewResizer(() => window);

            resizer.Resize(1400, 900);

            Assert.False(window.IsVisible);
            Assert.Equal(1000, window.Width);
            Assert.Equal(700, window.Height);
        });
    }

    [Fact]
    public void Resize_WhenWindowMinimizedOrMaximized_DoesNotResize()
    {
        WpfTestHost.Run(() =>
        {
            var window = new System.Windows.Window
            {
                Width = 1000,
                Height = 700
            };
            using var offscreen = WpfTestHost.ShowOffscreen(window);
            var resizer = new DashboardPreviewResizer(() => window);

            // Minimized:
            window.WindowState = System.Windows.WindowState.Minimized;
            var w = window.Width;
            var h = window.Height;
            resizer.Resize(1400, 900);
            Assert.Equal(w, window.Width);
            Assert.Equal(h, window.Height);
            Assert.NotEqual(1400, window.Width);
            Assert.NotEqual(900, window.Height);

            // Maximized:
            window.WindowState = System.Windows.WindowState.Maximized;
            w = window.Width;
            h = window.Height;
            resizer.Resize(1400, 900);
            Assert.Equal(w, window.Width);
            Assert.Equal(h, window.Height);
            Assert.NotEqual(1400, window.Width);
            Assert.NotEqual(900, window.Height);
        });
    }

    [Fact]
    public void Resize_WhenWindowIsNull_DoesNotThrow()
    {
        var resizer = new DashboardPreviewResizer(() => null);
        var ex = Record.Exception(() => resizer.Resize(1400, 900));
        Assert.Null(ex);
    }
}
