namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>
/// Represents a system tray host that can receive provider readings and be disposed.
/// </summary>
public interface ITrayHost : ITrayReadingsSink, IDisposable
{
}
