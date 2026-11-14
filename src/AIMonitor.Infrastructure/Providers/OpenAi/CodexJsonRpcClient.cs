using System.Text.Json;
using System.Threading.Channels;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Frames the Codex App Server's line-delimited JSON-RPC-like protocol over an already-open
/// stdin/stdout pair: one JSON object per line, requests carry an auto-incrementing integer
/// <c>id</c>, and a background loop reads every line the child prints so a matching response can be
/// picked out from unrelated notifications and out-of-order replies. Depends only on
/// <see cref="TextWriter"/>/<see cref="TextReader"/> - no process or filesystem knowledge at all -
/// so it is fully testable against in-memory streams (see <c>CodexJsonRpcClientTests</c>).
/// <para>
/// This app never answers a server-initiated request with real work: every one is rejected with a
/// fixed, safe JSON-RPC error, and a refresh-token request specifically is treated as "this login no
/// longer works" (PAR-009: never refresh the original login).
/// </para>
/// </summary>
internal sealed class CodexJsonRpcClient
{
    private const string RefreshMethodName = "account/chatgptAuthTokens/refresh";

    private const string RejectionMessage = "Read-only monitor; sign in with Codex again.";

    private const string RefreshRejectedMessage =
        "Codex login expired or was rejected. Open Codex to refresh the login, then refresh here.";

    private const string StoppedMessage = "Codex App Server stopped. Update Codex and try again.";

    private const string TimedOutMessage = "Codex usage request timed out. Try refreshing again.";

    private const string InvalidResultMessage = "Codex returned an invalid usage response.";

    /// <summary>Caps a single stdout line independent of its declared or actual length, so a
    /// misbehaving child that writes an unbounded line with no newline cannot force unbounded memory
    /// use in the background reader loop.</summary>
    internal const int MaxLineLength = 1_000_000;

    private readonly TextWriter _standardInput;
    private readonly Channel<JsonDocument> _channel =
        Channel.CreateBounded<JsonDocument>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });

    private long _sequence;

    public CodexJsonRpcClient(TextWriter standardInput, TextReader standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);

        _standardInput = standardInput;
        ReaderCompletion = Task.Run(() => ReadLoopAsync(standardOutput));
    }

    /// <summary>
    /// Completes once the background reader loop has stopped (stdout reached EOF, or the pipe broke).
    /// A caller awaits this briefly during cleanup so any reader-loop exception is observed rather
    /// than silently lost, matching the Python baseline's bounded <c>reader.join(timeout=1)</c>.
    /// </summary>
    public Task ReaderCompletion { get; }

    /// <summary>Sends a one-way message with no <c>id</c> - the server never replies to it.</summary>
    public Task SendNotificationAsync(string method, CancellationToken cancellationToken) =>
        SendAsync(new { method }, cancellationToken);

    /// <summary>
    /// Sends a request and waits for its matching response. Every message read in between that is not
    /// the match - a notification, a reply to an earlier/unrelated id, or a server-initiated request -
    /// is handled and the wait continues, exactly as production traffic requires: the App Server is
    /// free to interleave notifications with the answer to any single call.
    /// </summary>
    /// <exception cref="CodexAppServerException">The server reported an error, the child stopped, the
    /// call timed out, or the server asked this read-only client to refresh the login.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was
    /// cancelled.</exception>
    public async Task<JsonElement> CallAsync(
        string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var requestId = Interlocked.Increment(ref _sequence);
        try
        {
            // Shares the same deadline as the response wait below: a child that never drains its own
            // stdin (so the write/flush blocks on OS pipe backpressure) must time out here too, not
            // hang forever before the response-wait loop even starts.
            await SendAsync(
                parameters is null
                    ? new { id = requestId, method }
                    : new { id = requestId, method, @params = parameters },
                linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CodexAppServerException(TimedOutMessage);
        }

        while (true)
        {
            bool hasMore;
            try
            {
                hasMore = await _channel.Reader.WaitToReadAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new CodexAppServerException(TimedOutMessage);
            }

            if (!hasMore)
            {
                throw new CodexAppServerException(StoppedMessage);
            }

            if (!_channel.Reader.TryRead(out var document))
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var hasId = root.TryGetProperty("id", out var idElement);
                var hasMethod = root.TryGetProperty("method", out var methodElement);

                if (hasMethod && hasId)
                {
                    // A server-initiated request. Explicitly refuse it and every other one: this
                    // client never does real work for the server, and a refresh request specifically
                    // means the login this call is using no longer works.
                    var serverMethod = methodElement.ValueKind == JsonValueKind.String ? methodElement.GetString() : null;
                    try
                    {
                        await SendRejectionAsync(idElement, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new CodexAppServerException(TimedOutMessage);
                    }

                    if (serverMethod == RefreshMethodName)
                    {
                        throw new CodexAppServerException(RefreshRejectedMessage, unauthorized: true);
                    }

                    continue;
                }

                if (hasMethod)
                {
                    continue; // a notification - nothing to reply to, nothing this call is waiting for
                }

                if (!hasId || !IdMatches(idElement, requestId))
                {
                    continue; // a reply to a different (earlier/unrelated) request
                }

                if (root.TryGetProperty("error", out _))
                {
                    // The server's own error object may include untrusted/sensitive text, so only a
                    // fixed message naming the (locally-known) method we sent is ever surfaced.
                    throw new CodexAppServerException(
                        $"Codex could not complete {method}. Check your connection, update Codex, and sign in again if needed.");
                }

                if (!root.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Object)
                {
                    throw new CodexAppServerException(InvalidResultMessage);
                }

                return resultElement.Clone();
            }
        }
    }

    private static bool IdMatches(JsonElement idElement, long requestId) =>
        idElement.ValueKind == JsonValueKind.Number
        && idElement.TryGetInt64(out var value)
        && value == requestId;

    private Task SendRejectionAsync(JsonElement id, CancellationToken cancellationToken) =>
        SendAsync(new { id, error = new { code = -32601, message = RejectionMessage } }, cancellationToken);

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(message);
        await _standardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _standardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(TextReader standardOutput)
    {
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await BoundedLineReader
                        .ReadLineAsync(standardOutput, MaxLineLength, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (LineTooLongException)
                {
                    // Treated the same as a line that fails to parse below: skipped rather than
                    // treated as a protocol failure, so one pathological line cannot end the client
                    // or grow this loop's memory use without bound.
                    continue;
                }

                if (line is null)
                {
                    return; // EOF: the child closed stdout
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // A line that is not valid JSON is skipped rather than treated as a protocol
                    // failure, matching the Python baseline: one bad line must not end the client.
                    continue;
                }

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    continue;
                }

                await _channel.Writer.WriteAsync(document).ConfigureAwait(false);
            }
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }
}
