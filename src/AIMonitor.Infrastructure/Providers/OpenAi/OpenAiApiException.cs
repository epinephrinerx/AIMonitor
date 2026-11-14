using System.Net;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// An OpenAI Admin API HTTP call failed in a way that must become a safe, actionable error snapshot.
/// <see cref="Message"/> is always a fixed or narrowly-templated string (interpolating at most an HTTP
/// status code) - the response body is never echoed, since an arbitrary server-provided message is
/// untrusted.
/// </summary>
internal sealed class OpenAiApiException : Exception
{
    public bool Unauthorized { get; }
    public HttpStatusCode? StatusCode { get; }

    public OpenAiApiException(string message, bool unauthorized = false, HttpStatusCode? statusCode = null)
        : base(message)
    {
        Unauthorized = unauthorized;
        StatusCode = statusCode;
    }
}
