using Avalonia;

namespace AIMonitor.Presentation.Avalonia;

internal static class Program
{
    /// <summary>
    /// Entry point. The single-instance guard runs before any window is built, so a second launch
    /// hands its arguments to the copy already running and exits without flashing a window
    /// (PAR-023).
    /// </summary>
    public static int Main(string[] args)
    {
        var platform = PlatformHost.Create();
        if (!platform.TryAcquireSingleInstance(args, out var exitCode))
        {
            return exitCode;
        }

        try
        {
            return BuildAvaloniaApp(platform).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            platform.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Also used by the Avalonia previewer, which calls it with no host.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(PlatformHost? platform) =>
        AppBuilder.Configure(() => new App(platform, Environment.GetCommandLineArgs()[1..]))
            .UsePlatformDetect()
            // No font is named on purpose: macOS has no Segoe UI, and asking for a font that is
            // not installed makes the toolkit substitute one glyph run at a time.
            .LogToTrace();
}
