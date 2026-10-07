using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;

namespace AIMonitor.Presentation.Wpf.Theme;

/// <summary>
/// Paints the native title bar with the app theme. WPF leaves the caption to Windows, which shows the system accent
/// colour (a brown title bar on some setups) and ignores the app's dark mode; DWM lets us set the caption, its text
/// and the dark/light frame explicitly. Needs Windows 11 for the colours; older builds just ignore the calls.
/// </summary>
public static class TitleBarTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Themes every window as it is created, and re-themes the open ones when the theme changes.</summary>
    public static void Install(ThemeManager themes)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender, themes.IsDark)));

        themes.ThemeChanged += () =>
        {
            foreach (Window window in System.Windows.Application.Current.Windows)
            {
                Apply(window, themes.IsDark);
            }
        };
    }

    public static void Apply(Window window, bool isDark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var palette = ThemePalette.For(isDark);
        Set(handle, DwmUseImmersiveDarkMode, isDark ? 1 : 0);
        Set(handle, DwmCaptionColor, ToColorRef(palette.Plane));
        Set(handle, DwmTextColor, ToColorRef(palette.Ink));
        Set(handle, DwmBorderColor, ToColorRef(palette.Baseline));
    }

    /// <summary>DWM takes a COLORREF: 0x00BBGGRR.</summary>
    internal static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private static void Set(IntPtr handle, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort: no DWM, no themed title bar.
        }
    }
}
