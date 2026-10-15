namespace AIMonitor.Infrastructure.Providers.Agy;

public enum AgyErrorKind
{
    General,
    NotInstalled,
    NotSignedIn,
    RateLimited,
    InvalidResponse,
    Timeout,
}

/// <summary>
/// Exception representing a known failure when interacting with or parsing AGY CLI usage.
/// Production callers supply fixed, safe messages so raw details or credentials are never leaked.
/// </summary>
public class AgyUsageException : Exception
{
    public AgyErrorKind Kind { get; }

    public AgyUsageException(string message, AgyErrorKind kind = AgyErrorKind.General)
        : base(message)
    {
        Kind = kind;
    }

    public AgyUsageException(string message, Exception innerException, AgyErrorKind kind = AgyErrorKind.General)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
