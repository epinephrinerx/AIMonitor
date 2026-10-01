namespace AIMonitor.Application.Windows;

/// <summary>
/// Controls whether the application is registered to start automatically when the user logs into Windows.
/// In accordance with PAR-024, the Windows Startup registration is per-user, and the Windows Registry /
/// Startup Apps configuration serves as the source of truth.
/// </summary>
public interface IStartupRegistrar
{
    /// <summary>
    /// Checks whether the application is currently registered to launch at Windows startup.
    /// </summary>
    bool IsRegistered();

    /// <summary>
    /// Registers the application to start with Windows.
    /// </summary>
    /// <param name="executablePath">The full path to the executable.</param>
    /// <param name="arguments">Optional launch arguments (e.g. --minimized or --tray).</param>
    void Register(string executablePath, string arguments = "");

    /// <summary>
    /// Removes the application from Windows startup.
    /// </summary>
    void Unregister();
}
