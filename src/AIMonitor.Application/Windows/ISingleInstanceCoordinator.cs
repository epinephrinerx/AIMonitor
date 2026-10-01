namespace AIMonitor.Application.Windows;

/// <summary>
/// Coordinates single-instance execution per Windows user.
/// In accordance with PAR-023 and ADR-0003, only one instance may run per Windows user session.
/// Subsequent launches activate the running primary instance and terminate cleanly.
/// </summary>
public interface ISingleInstanceCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Attempts to acquire the single-instance ownership.
    /// Returns <c>true</c> if this instance is the primary instance (acquired lock),
    /// or <c>false</c> if another instance is already running for this user.
    /// </summary>
    ValueTask<bool> TryAcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised on the primary instance when a secondary instance requests activation.
    /// Passes the command-line arguments sent by the secondary instance.
    /// </summary>
    event Action<string[]>? Activated;

    /// <summary>
    /// Signals the existing primary instance to activate and bring its window to the foreground.
    /// </summary>
    /// <param name="arguments">Command-line arguments from the activating process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the signal was delivered successfully; otherwise <c>false</c>.</returns>
    ValueTask<bool> NotifyExistingInstanceAsync(string[] arguments, CancellationToken cancellationToken = default);
}
