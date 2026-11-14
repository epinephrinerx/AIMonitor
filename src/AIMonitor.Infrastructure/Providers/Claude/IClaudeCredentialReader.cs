using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Injectable boundary for reading Claude Code's credentials file. Production code uses
/// <see cref="ClaudeCredentialFileReader"/>; tests can substitute a hand-written fake (e.g. one that
/// delays before returning) to exercise cancellation without touching the real filesystem's timing.
/// </summary>
public interface IClaudeCredentialReader
{
    /// <summary>
    /// Reads and parses the credentials file at <paramref name="path"/>. Never throws for an
    /// expected outcome (missing file, unreadable/malformed content, missing/blank/unsafe token,
    /// expired token) — those become a <see cref="ClaudeCredentialReadResult"/> instead. The only
    /// exception a caller should expect is <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<ClaudeCredentialReadResult> ReadAsync(string path, IClock clock, CancellationToken cancellationToken);
}
