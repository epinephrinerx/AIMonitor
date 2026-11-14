using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Injectable boundary for reading Codex CLI's auth file. Production code uses
/// <see cref="CodexOAuthFileReader"/>; tests can substitute a hand-written fake (e.g. one that delays
/// before returning) to exercise cancellation without touching the real filesystem's timing.
/// </summary>
public interface ICodexOAuthReader
{
    /// <summary>
    /// Reads and parses the auth file at <paramref name="path"/>. Never throws for an expected
    /// outcome (missing file, unreadable/malformed content, missing/blank/unsafe token, expired
    /// token) — those become a <see cref="CodexOAuthReadResult"/> instead. The only exception a
    /// caller should expect is <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<CodexOAuthReadResult> ReadAsync(string path, IClock clock, CancellationToken cancellationToken);
}
