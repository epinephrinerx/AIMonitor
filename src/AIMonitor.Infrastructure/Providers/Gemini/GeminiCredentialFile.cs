using System.Text;
using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// The single read-only, bounded, strict-UTF-8 file primitive every Gemini credential candidate
/// (a caller-selected service-account file, the <c>GOOGLE_APPLICATION_CREDENTIALS</c> file, Gemini
/// CLI's <c>oauth_creds.json</c>/<c>google_accounts.json</c>, and both gcloud application-default
/// locations) reads through, so their cancellation, size-bounding, and decoding behavior cannot drift
/// apart between candidates. Never creates, updates, renames, changes permissions on, or deletes
/// anything - this only ever opens a file for reading.
/// </summary>
internal static class GeminiCredentialFile
{
    /// <summary>Real credential/login files here are at most a few KB; this generously caps well
    /// above that while still bounding a pathological or malicious file.</summary>
    private const int MaxFileBytes = 1024 * 1024;

    private const int ChunkSize = 8192;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal enum ReadStatus
    {
        /// <summary>No file at the given path.</summary>
        Missing,

        /// <summary>File exists but could not be read as UTF-8 text within the size cap (I/O/access
        /// failure, or invalid UTF-8 bytes).</summary>
        Unreadable,

        /// <summary>File was read in full and decoded; <see cref="ReadResult.Text"/> holds its
        /// content, which may or may not turn out to be valid/usable JSON.</summary>
        Success,
    }

    internal sealed record ReadResult(ReadStatus Status, string? Text)
    {
        internal static readonly ReadResult Missing = new(ReadStatus.Missing, null);

        internal static readonly ReadResult Unreadable = new(ReadStatus.Unreadable, null);

        internal static ReadResult Success(string text) => new(ReadStatus.Success, text);
    }

    private static Stream OpenFile(string path) =>
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

    internal static Task<ReadResult> ReadTextAsync(string path, CancellationToken cancellationToken) =>
        ReadTextAsync(path, OpenFile, cancellationToken);

    /// <summary>Test-only seam: lets a test substitute a controlled/growing stream in place of the
    /// real file open, to exercise cancellation mid-read and the cumulative size cap deterministically.
    /// Production always goes through the single-argument overload, which uses <see cref="OpenFile"/>.</summary>
    internal static async Task<ReadResult> ReadTextAsync(
        string path, Func<string, Stream> openFile, CancellationToken cancellationToken)
    {
        // Checked before opening the file so an already-cancelled call touches nothing.
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes;
        try
        {
            var stream = openFile(path);
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

                    if (buffer.Length + bytesRead > MaxFileBytes)
                    {
                        return ReadResult.Unreadable;
                    }

                    buffer.Write(chunk, 0, bytesRead);
                }

                bytes = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReadResult.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ReadResult.Unreadable;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ReadResult.Success(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return ReadResult.Unreadable;
        }
    }

    /// <summary>
    /// Parses <paramref name="json"/> into an object-rooted document, or <see langword="null"/> for
    /// invalid JSON syntax or a non-object root - collapsing both into "nothing usable here". Matches
    /// the Python baseline's shared <c>read_json()</c>, used by every Gemini candidate except the two
    /// that keep a syntax error distinct from a valid-but-incomplete shape so their message can say
    /// which one happened (see <see cref="GeminiServiceAccountCredentialReader"/>).
    /// </summary>
    internal static JsonDocument? TryParseObject(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        return document;
    }
}
