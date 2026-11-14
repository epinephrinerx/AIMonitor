using System.Text;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>Thrown by <see cref="BoundedLineReader.ReadLineAsync"/> when a line grows past the
/// configured character budget before a newline (or EOF) is found. The offending line has already
/// been fully drained from the reader by the time this is thrown, so the next call resumes cleanly at
/// the following line.</summary>
internal sealed class LineTooLongException : Exception;

/// <summary>
/// Reads one line at a time from a <see cref="TextReader"/>, the same as
/// <see cref="TextReader.ReadLineAsync()"/>, except a line that grows past a fixed character budget
/// before its terminator throws <see cref="LineTooLongException"/> instead of buffering an unbounded
/// amount of text - protection against a misbehaving child process that writes a huge line with no
/// newline. Used to bound <see cref="CodexJsonRpcClient"/>'s stdout framing.
/// </summary>
internal static class BoundedLineReader
{
    /// <summary>
    /// Reads characters one at a time (cheap here: <see cref="TextReader"/> instances backed by a real
    /// pipe/file, i.e. <see cref="StreamReader"/>, already buffer internally, so this does not issue a
    /// syscall per character) until a line feed, EOF, or the length budget is exceeded.
    /// </summary>
    public static async Task<string?> ReadLineAsync(TextReader reader, int maxLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "Must be positive.");
        }

        var builder = new StringBuilder();
        var overLength = false;
        var sawAnyChar = false;
        var single = new char[1];

        while (true)
        {
            var read = await reader.ReadAsync(single.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (!sawAnyChar)
                {
                    return null;
                }

                break; // EOF ends the final (possibly partial) line, matching TextReader.ReadLineAsync.
            }

            sawAnyChar = true;
            var ch = single[0];
            if (ch == '\n')
            {
                break;
            }

            if (overLength)
            {
                continue; // Already over budget - keep draining this line without buffering more of it.
            }

            if (builder.Length >= maxLength)
            {
                overLength = true;
                continue;
            }

            builder.Append(ch);
        }

        if (overLength)
        {
            throw new LineTooLongException();
        }

        if (builder.Length > 0 && builder[^1] == '\r')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
