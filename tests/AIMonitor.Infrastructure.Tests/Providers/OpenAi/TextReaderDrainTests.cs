using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// Exercises <see cref="TextReaderDrain"/> against in-memory <see cref="TextReader"/> fakes - never a
/// real process or pipe. Proves the drain loop actually keeps reading across multiple chunks (the
/// property that prevents OS pipe backpressure in <see cref="CodexProcess"/>'s real stderr use) and
/// that it never lets an exception escape, regardless of when the underlying stream fails.
/// </summary>
public sealed class TextReaderDrainTests
{
    /// <summary>Yields <paramref name="totalChars"/> synthetic characters before reporting EOF,
    /// tracking how many <see cref="ReadAsync(Memory{char}, CancellationToken)"/> calls it took - used
    /// to prove the drain loop reads in bounded chunks rather than one unbounded read.</summary>
    private sealed class CountingTextReader(int totalChars) : TextReader
    {
        private int _remaining = totalChars;

        public int ReadCallCount { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken)
        {
            ReadCallCount++;
            if (_remaining == 0)
            {
                return ValueTask.FromResult(0);
            }

            var toWrite = Math.Min(buffer.Length, _remaining);
            buffer.Span[..toWrite].Fill('x');
            _remaining -= toWrite;
            return ValueTask.FromResult(toWrite);
        }
    }

    private sealed class ThrowingTextReader(Exception exception) : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromException<int>(exception);
    }

    [Fact]
    public async Task DiscardAsync_DrainsMultipleChunksUntilEof()
    {
        // Several times the internal chunk size (4096), so a single unbounded read would prove
        // nothing - only looping across chunks proves the pipe is actually kept drained.
        var reader = new CountingTextReader(totalChars: 20_000);

        await TextReaderDrain.DiscardAsync(reader);

        Assert.True(reader.ReadCallCount >= 5, $"expected several chunked reads, got {reader.ReadCallCount}");
    }

    [Fact]
    public async Task DiscardAsync_EmptyReader_CompletesImmediately()
    {
        var reader = new CountingTextReader(totalChars: 0);

        await TextReaderDrain.DiscardAsync(reader);

        Assert.Equal(1, reader.ReadCallCount);
    }

    [Fact]
    public async Task DiscardAsync_ReaderThrowsIOException_CompletesQuietlyWithoutPropagating()
    {
        var reader = new ThrowingTextReader(new IOException("pipe closed"));

        await TextReaderDrain.DiscardAsync(reader);
    }

    [Fact]
    public async Task DiscardAsync_ReaderThrowsObjectDisposedException_CompletesQuietlyWithoutPropagating()
    {
        var reader = new ThrowingTextReader(new ObjectDisposedException(nameof(TextReader)));

        await TextReaderDrain.DiscardAsync(reader);
    }
}
