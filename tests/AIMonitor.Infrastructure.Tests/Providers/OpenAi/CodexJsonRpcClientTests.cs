using System.Text;
using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// Exercises <see cref="CodexJsonRpcClient"/>'s line-delimited JSON-RPC framing against in-memory
/// <see cref="StringWriter"/>/<see cref="TextReader"/> pairs - never a real process. Covers PAR-009's
/// protocol requirements: ignoring notifications and unrelated replies, refusing every server-initiated
/// request (a refresh request specifically becomes an actionable, unauthorized error) without ever
/// echoing a token, never leaking the server's own error text, and distinguishing a caller cancellation
/// from an adapter timeout.
/// </summary>
[Trait("Category", "Contract")]
public sealed class CodexJsonRpcClientTests
{
    private static readonly TimeSpan AmpleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A stdout that never produces a byte, simulating a process that has not answered yet -
    /// used to exercise timeout/cancellation without racing real process I/O timing. Overrides the
    /// <see cref="ReadAsync(Memory{char}, CancellationToken)"/> overload specifically, since that is
    /// what <see cref="BoundedLineReader"/> actually calls.</summary>
    private sealed class NeverRespondingTextReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken) =>
            new(new TaskCompletionSource<int>().Task);
    }

    /// <summary>A stdin whose writes/flushes never complete until cancelled, simulating a child that
    /// is not draining its own stdin (OS pipe backpressure) - used to prove the call timeout covers
    /// the send side, not just the response wait.</summary>
    private sealed class NeverFlushingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static StringReader LinesOf(params string[] lines) => new(string.Join('\n', lines) + "\n");

    [Fact]
    public async Task CallAsync_IgnoresNotificationsAndUnrelatedRepliesAndMatchesById()
    {
        var stdout = LinesOf(
            """{"method":"account/updated"}""",
            """{"id":90,"result":{}}""",
            """{"id":1,"result":{"ok":true}}""");
        var client = new CodexJsonRpcClient(new StringWriter(), stdout);

        var result = await client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None);

        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task CallAsync_RefusesServerInitiatedRefreshRequest_AndEchoesTheServersIdVerbatim()
    {
        var stdout = LinesOf("""{"id":"server-1","method":"account/chatgptAuthTokens/refresh"}""");
        var stdin = new StringWriter();
        var client = new CodexJsonRpcClient(stdin, stdout);

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None));

        Assert.True(ex.Unauthorized);
        var sent = stdin.ToString();
        Assert.Contains("Read-only monitor", sent, StringComparison.Ordinal);
        Assert.Contains("\"server-1\"", sent, StringComparison.Ordinal); // echoed as a JSON string, not an int
        Assert.DoesNotContain("refresh", sent, StringComparison.OrdinalIgnoreCase); // never grants what was asked
    }

    [Fact]
    public async Task CallAsync_RefusesOrdinaryServerRequest_WithoutTreatingItAsFatal()
    {
        // A non-refresh server request is rejected the same way, but the call keeps waiting for its
        // own answer rather than failing outright.
        var stdout = LinesOf(
            """{"id":5,"method":"some/other/request"}""",
            """{"id":1,"result":{"ok":true}}""");
        var stdin = new StringWriter();
        var client = new CodexJsonRpcClient(stdin, stdout);

        var result = await client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Contains("\"code\":-32601", stdin.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_ServerErrorObject_NeverLeaksItsMessageText()
    {
        var stdout = LinesOf("""{"id":1,"error":{"message":"secret-bearer-token"}}""");
        var client = new CodexJsonRpcClient(new StringWriter(), stdout);

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None));

        Assert.DoesNotContain("secret-bearer-token", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.Unauthorized);
        Assert.Contains("account/rateLimits/read", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_NonObjectResult_ThrowsInvalidResponse()
    {
        var stdout = LinesOf("""{"id":1,"result":"not-an-object"}""");
        var client = new CodexJsonRpcClient(new StringWriter(), stdout);

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None));

        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallAsync_MissingResultAndError_ThrowsInvalidResponse()
    {
        var stdout = LinesOf("""{"id":1}""");
        var client = new CodexJsonRpcClient(new StringWriter(), stdout);

        await Assert.ThrowsAsync<CodexAppServerException>(
            () => client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None));
    }

    [Fact]
    public async Task CallAsync_SkipsMalformedJsonLinesAndNonObjectLines()
    {
        var stdout = LinesOf("not-json-at-all", "[1,2,3]", """{"id":1,"result":{"ok":true}}""");
        var client = new CodexJsonRpcClient(new StringWriter(), stdout);

        var result = await client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None);

        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task CallAsync_StdoutClosesBeforeAnyResponse_ThrowsStopped()
    {
        var client = new CodexJsonRpcClient(new StringWriter(), new StringReader(string.Empty));

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None));

        Assert.Contains("stopped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallAsync_NoResponseWithinTimeout_ThrowsTimedOut()
    {
        var client = new CodexJsonRpcClient(new StringWriter(), new NeverRespondingTextReader());

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(() => client.CallAsync(
            "account/rateLimits/read", null, TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.Unauthorized);
    }

    [Fact]
    public async Task CallAsync_SendHangs_TimesOutRatherThanHangingForever()
    {
        // Before the fix, only the response wait was bounded by `timeout` - a stdin write/flush that
        // never completes (e.g. the child not draining its own stdin) hung this call forever.
        var client = new CodexJsonRpcClient(new NeverFlushingTextWriter(), new NeverRespondingTextReader());

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(() => client.CallAsync(
            "account/rateLimits/read", null, TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallAsync_OversizedStdoutLine_IsSkippedAndFramingResyncsToTheNextLine()
    {
        var oversized = new string('a', CodexJsonRpcClient.MaxLineLength + 1);
        var stdout = LinesOf(oversized, """{"id":1,"result":{"ok":true}}""");
        var client = new CodexJsonRpcClient(new StringWriter(), stdout);

        var result = await client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None);

        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task CallAsync_AlreadyCancelled_ThrowsWithoutSendingAnything()
    {
        var stdin = new StringWriter();
        var client = new CodexJsonRpcClient(stdin, new NeverRespondingTextReader());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CallAsync("account/rateLimits/read", null, AmpleTimeout, cts.Token));

        Assert.Empty(stdin.ToString());
    }

    [Fact]
    public async Task CallAsync_CallerCancelsWhileWaiting_PropagatesCancellationNotTimeout()
    {
        var client = new CodexJsonRpcClient(new StringWriter(), new NeverRespondingTextReader());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        // A generously long call timeout: only the caller's own token should be able to end this wait.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallAsync(
            "account/rateLimits/read", null, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task CallAsync_SendsAutoIncrementingIdMethodAndParams()
    {
        var stdin = new StringWriter();
        var client = new CodexJsonRpcClient(stdin, LinesOf("""{"id":1,"result":{}}""", """{"id":2,"result":{}}"""));

        await client.CallAsync(
            "account/login/start", new { type = "chatgptAuthTokens", accessToken = "tok" }, AmpleTimeout, CancellationToken.None);
        await client.CallAsync("account/rateLimits/read", null, AmpleTimeout, CancellationToken.None);

        var sent = stdin.ToString();
        Assert.Contains("\"id\":1", sent, StringComparison.Ordinal);
        Assert.Contains("\"id\":2", sent, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"account/login/start\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"params\":{\"type\":\"chatgptAuthTokens\",\"accessToken\":\"tok\"}", sent, StringComparison.Ordinal);
        // The second call passes no parameters, matching the protocol's "params" field being entirely
        // absent (not null) for a no-argument request.
        Assert.Contains("\"method\":\"account/rateLimits/read\"}", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendNotificationAsync_WritesMethodOnlyWithoutAnId()
    {
        var stdin = new StringWriter();
        var client = new CodexJsonRpcClient(stdin, new StringReader(string.Empty));

        await client.SendNotificationAsync("initialized", CancellationToken.None);

        Assert.Equal("""{"method":"initialized"}""", stdin.ToString().Trim());
    }
}
