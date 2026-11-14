namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>Outcome of trying to read the Claude Code credentials file.</summary>
public enum ClaudeCredentialStatus
{
    /// <summary>No file at the resolved path.</summary>
    Missing,

    /// <summary>File exists but is unreadable, not valid JSON, not an object, or has no usable
    /// (present, string, non-blank) access token.</summary>
    Invalid,

    /// <summary>A usable access token was found, but its <c>expiresAt</c> is at or before the
    /// clock's current instant.</summary>
    Expired,

    /// <summary>A usable, unexpired access token was found.</summary>
    Found,
}

/// <summary>
/// Discriminated outcome of a credential read, in place of exception-driven control flow for the
/// expected missing/invalid/expired cases. <see cref="Credentials"/> is populated for
/// <see cref="ClaudeCredentialStatus.Found"/> and <see cref="ClaudeCredentialStatus.Expired"/> only.
/// </summary>
public sealed record ClaudeCredentialReadResult
{
    public ClaudeCredentialStatus Status { get; }

    public string Path { get; }

    public ClaudeCredentials? Credentials { get; }

    private ClaudeCredentialReadResult(ClaudeCredentialStatus status, string path, ClaudeCredentials? credentials)
    {
        Status = status;
        Path = path;
        Credentials = credentials;
    }

    public static ClaudeCredentialReadResult Missing(string path) =>
        new(ClaudeCredentialStatus.Missing, path, credentials: null);

    public static ClaudeCredentialReadResult Invalid(string path) =>
        new(ClaudeCredentialStatus.Invalid, path, credentials: null);

    public static ClaudeCredentialReadResult Expired(ClaudeCredentials credentials, string path) =>
        new(ClaudeCredentialStatus.Expired, path, credentials);

    public static ClaudeCredentialReadResult Found(ClaudeCredentials credentials, string path) =>
        new(ClaudeCredentialStatus.Found, path, credentials);
}
