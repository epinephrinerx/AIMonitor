namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// A Codex App Server call failed in a way that must become a safe, actionable error snapshot rather
/// than propagate. <see cref="Message"/> is always a fixed or narrowly-templated string (interpolating
/// at most a protocol method name this app itself sent) - never server-provided error text, a
/// credential, or a response payload.
/// </summary>
public sealed class CodexAppServerException : Exception
{
    public bool Unauthorized { get; }

    public CodexAppServerException(string message, bool unauthorized = false)
        : base(message)
    {
        Unauthorized = unauthorized;
    }
}
