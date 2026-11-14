using System.Text;
using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Reads Codex CLI's auth file, read-only: it never creates, updates, refreshes, renames, changes
/// permissions on, or deletes the file. Opens the file exactly once (no separate
/// <see cref="File.Exists(string)"/> check, which would leave a race window between the check and
/// the open) and classifies whatever the open/read raises: a missing file or missing containing
/// directory means <see cref="CodexOAuthStatus.Missing"/>; any other I/O or access failure means
/// <see cref="CodexOAuthStatus.Invalid"/>. The file's byte length is capped independent of its
/// declared size so a pathological or malicious file cannot force unbounded memory use, and the
/// bytes are decoded with a strict UTF-8 decoder that rejects invalid sequences instead of silently
/// substituting them.
/// </summary>
public sealed class CodexOAuthFileReader : ICodexOAuthReader
{
    /// <summary>Real Codex auth files are a few KB; this generously caps well above that while still
    /// bounding a pathological or malicious file.</summary>
    private const int MaxAuthFileBytes = 1024 * 1024;

    private const int ChunkSize = 8192;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Func<string, Stream> _openFile;

    public CodexOAuthFileReader()
        : this(OpenFile)
    {
    }

    /// <summary>Test-only seam: lets a test substitute a controlled/growing stream in place of the
    /// real file open, to exercise the cumulative size cap deterministically. Production always uses
    /// <see cref="OpenFile"/>, preserving the single, open-once behavior.</summary>
    internal CodexOAuthFileReader(Func<string, Stream> openFile)
    {
        _openFile = openFile;
    }

    private static Stream OpenFile(string path) =>
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

    public async Task<CodexOAuthReadResult> ReadAsync(string path, IClock clock, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(clock);

        // Checked before opening the file so an already-cancelled call touches nothing.
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes;
        try
        {
            var stream = _openFile(path);
            await using (stream.ConfigureAwait(false))
            {
                // Read in bounded chunks and enforce the cumulative cap before every write, rather
                // than trusting the stream's reported Length up front: a file or stream that grows
                // after opening (or lies about its length) must still never be read past the cap.
                using var buffer = new MemoryStream();
                var chunk = new byte[ChunkSize];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (buffer.Length + bytesRead > MaxAuthFileBytes)
                    {
                        return CodexOAuthReadResult.Invalid(path);
                    }

                    buffer.Write(chunk, 0, bytesRead);
                }

                bytes = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return CodexOAuthReadResult.Missing(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CodexOAuthReadResult.Invalid(path);
        }

        cancellationToken.ThrowIfCancellationRequested();

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return CodexOAuthReadResult.Invalid(path);
        }

        var result = CodexOAuthParser.Parse(json, path, clock);

        // Rechecked after parsing (a pure, synchronous, non-trivial-cost step) so a caller
        // cancellation that arrives during parsing is still observed before this method returns.
        cancellationToken.ThrowIfCancellationRequested();

        return result;
    }
}
