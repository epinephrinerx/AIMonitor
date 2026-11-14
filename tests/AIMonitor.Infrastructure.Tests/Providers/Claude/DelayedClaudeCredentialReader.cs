using AIMonitor.Application.Time;
using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Hand-written fake <see cref="IClaudeCredentialReader"/> that waits on
/// <see cref="CancellationToken"/> forever (never touching the real filesystem or a timer), so a
/// test can prove <see cref="ClaudeLiveQuotaClient"/> propagates caller cancellation that arrives
/// while a credential read is still in flight.
/// </summary>
internal sealed class DelayedClaudeCredentialReader : IClaudeCredentialReader
{
    public async Task<ClaudeCredentialReadResult> ReadAsync(string path, IClock clock, CancellationToken cancellationToken)
    {
        // Never completes on its own; only cancellation ends this wait, which throws
        // OperationCanceledException/TaskCanceledException out of this method as a real reader's
        // cancellable I/O would.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return ClaudeCredentialReadResult.Missing(path);
    }
}
