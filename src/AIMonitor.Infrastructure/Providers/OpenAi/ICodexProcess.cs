namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Minimal seam over a running Codex App Server child process. Production uses
/// <see cref="CodexProcess"/> (a real <see cref="System.Diagnostics.Process"/>); tests substitute a
/// hand-written fake so process orchestration (temporary home, environment sanitization, cleanup
/// ordering) is verified without ever launching a real subprocess.
/// </summary>
internal interface ICodexProcess : IDisposable
{
    /// <summary>The child's stdin. The protocol layer writes one JSON line per call and flushes;
    /// nothing here ever writes the refresh token or the original Codex config.</summary>
    TextWriter StandardInput { get; }

    /// <summary>The child's stdout, read one JSON line per message.</summary>
    TextReader StandardOutput { get; }

    /// <summary><see langword="true"/> once the process has exited on its own.</summary>
    bool HasExited { get; }

    /// <summary>Ends the process. Windows has no separate graceful-terminate signal for a console
    /// subprocess distinct from this (unlike POSIX's SIGTERM/SIGKILL split), so there is exactly one
    /// termination step, not a terminate-then-kill escalation. Safe to call when the process has
    /// already exited on its own.</summary>
    void Kill();

    /// <summary>Waits up to <paramref name="timeout"/> for the process to exit (after <see cref="Kill"/>
    /// or on its own). Returns <see langword="true"/> if it exited within the budget.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout);
}
