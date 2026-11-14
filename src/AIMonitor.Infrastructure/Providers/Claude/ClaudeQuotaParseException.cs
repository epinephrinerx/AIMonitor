namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Thrown when a Claude quota payload cannot be parsed into <see cref="Domain.Meter"/> instances.
/// Every message is a fixed string, never interpolated with payload-derived kind/key/display-name
/// values, and no inner exception carrying untrusted content is ever attached — the entire
/// <see cref="Exception.ToString"/> chain is safe to log.
/// </summary>
public sealed class ClaudeQuotaParseException : Exception
{
    public ClaudeQuotaParseException(string message)
        : base(message)
    {
    }

    public ClaudeQuotaParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
