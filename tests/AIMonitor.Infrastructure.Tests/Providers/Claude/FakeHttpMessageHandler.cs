namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Hand-written fake transport: never touches the network. Captures the last request's method,
/// URI, and headers synchronously during <see cref="SendAsync"/>, before the caller has a chance to
/// dispose the request, so assertions on <see cref="LastRequest"/> stay valid afterward.
/// </summary>
internal sealed class FakeHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    /// <summary>Set when this handler is disposed, so a test can prove the client under test never
    /// disposes a caller-owned handler/<see cref="HttpClient"/>.</summary>
    public bool Disposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        return await responder(request, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disposed = true;
        }

        base.Dispose(disposing);
    }
}

/// <summary>Hand-written <see cref="HttpContent"/> that records whether it was disposed, so a test
/// can prove the client under test disposes the response content it read.</summary>
internal sealed class DisposalTrackingContent(string content) : StringContent(content)
{
    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disposed = true;
        }

        base.Dispose(disposing);
    }
}
