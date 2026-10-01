namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// A Gemini token-exchange, project-discovery, or Cloud Monitoring HTTP call failed in a way that must
/// become a safe, actionable error snapshot. <see cref="Message"/> is always a fixed or
/// narrowly-templated string (interpolating at most a host name or an HTTP status code) - the response
/// body is never echoed, since an arbitrary server-provided message is untrusted.
/// </summary>
internal sealed class GeminiApiException : Exception
{
    public bool Unauthorized { get; }

    public GeminiApiException(string message, bool unauthorized = false)
        : base(message)
    {
        Unauthorized = unauthorized;
    }
}
