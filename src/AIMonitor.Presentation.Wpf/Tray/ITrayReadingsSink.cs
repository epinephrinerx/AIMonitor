namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>
/// Sink for receiving provider tray readings.
/// </summary>
public interface ITrayReadingsSink
{
    /// <summary>
    /// Updates the sink with the latest provider readings.
    /// </summary>
    /// <param name="readings">The collection of latest provider readings.</param>
    void UpdateReadings(IEnumerable<TrayReading> readings);
}
