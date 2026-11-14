namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>Outcome of trying to read Codex's ChatGPT OAuth login from its auth file.</summary>
public enum CodexOAuthStatus
{
    /// <summary>No file at the resolved path.</summary>
    Missing,

    /// <summary>File exists but is unreadable, not valid JSON, not an object, has no <c>tokens</c>
    /// object, or has no usable (present, string, non-blank, control-character-free) access token.
    /// Treated identically to <see cref="Missing"/> by selection, matching the baseline: a corrupt
    /// auth file is not a signed-in-but-broken login, it is "nothing found here".</summary>
    Invalid,

    /// <summary>A usable access token was found, but its <c>exp</c> claim is at or before the clock's
    /// current instant.</summary>
    Expired,

    /// <summary>A usable access token was found and is not expired. The credential may still be
    /// <see cref="CodexOAuthCredential.IsUsageCapable"/> <see langword="false"/> (no account ID).</summary>
    Found,
}

/// <summary>
/// Discriminated outcome of a Codex OAuth read, in place of exception-driven control flow for the
/// expected missing/invalid/expired cases. <see cref="Credential"/> is populated for
/// <see cref="CodexOAuthStatus.Found"/> and <see cref="CodexOAuthStatus.Expired"/> only.
/// </summary>
public sealed record CodexOAuthReadResult
{
    public CodexOAuthStatus Status { get; }

    public string Path { get; }

    public CodexOAuthCredential? Credential { get; }

    private CodexOAuthReadResult(CodexOAuthStatus status, string path, CodexOAuthCredential? credential)
    {
        Status = status;
        Path = path;
        Credential = credential;
    }

    public static CodexOAuthReadResult Missing(string path) =>
        new(CodexOAuthStatus.Missing, path, credential: null);

    public static CodexOAuthReadResult Invalid(string path) =>
        new(CodexOAuthStatus.Invalid, path, credential: null);

    public static CodexOAuthReadResult Expired(CodexOAuthCredential credential, string path) =>
        new(CodexOAuthStatus.Expired, path, credential);

    public static CodexOAuthReadResult Found(CodexOAuthCredential credential, string path) =>
        new(CodexOAuthStatus.Found, path, credential);
}
