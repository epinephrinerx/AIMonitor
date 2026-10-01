namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Continuously reads and discards everything from a <see cref="TextReader"/> until it reaches EOF or
/// throws, without ever buffering or exposing what it read. Used to drain <see cref="CodexProcess"/>'s
/// redirected stderr: once a stream is redirected (<see cref="System.Diagnostics.ProcessStartInfo.RedirectStandardError"/>),
/// the OS pipe backing it has a bounded buffer - if nothing ever reads from it, the child process
/// blocks the moment it writes enough to fill that buffer, which would otherwise be able to hang the
/// whole App Server session on unrelated diagnostic output. Nothing in this app needs stderr's
/// content, and it may carry untrusted or sensitive text, so it is discarded rather than logged or
/// surfaced anywhere.
/// </summary>
internal static class TextReaderDrain
{
    private const int ChunkSize = 4096;

    public static async Task DiscardAsync(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var buffer = new char[ChunkSize];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0)
            {
                // Intentionally discarded: draining the pipe is the only goal.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Best-effort: the pipe closing (the child exiting, or this side disposing the stream)
            // ends the drain quietly - there is nothing left to read and nothing to report.
        }
    }
}
