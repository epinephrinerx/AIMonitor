using System.Diagnostics;

namespace AIMonitor.Infrastructure.Providers.Gemini;

/// <summary>
/// One Google credential Gemini detection found, plus where it came from. <see cref="Value"/> is
/// secret-shaped - a Gemini CLI OAuth bearer token, or the filesystem path to a service-account key
/// that will later be re-read to sign a token - and must never appear in <see cref="ToString"/>, an
/// exception message, a log line, or a debugger tooltip, so every one of those surfaces is redacted
/// here rather than left to the default record-generated representation. Matching the Python
/// baseline, <see cref="Value"/> is deliberately empty whenever <see cref="UsageCapable"/> is
/// <see langword="false"/> for a service-account candidate: an unusable key's path is withheld so
/// nothing downstream can try to sign with it. This type is Infrastructure-internal and never travels
/// into a <see cref="Domain.DetectionInfo"/> - that carries only the account/state metadata a caller
/// needs.
/// </summary>
[DebuggerDisplay("GeminiCredential (redacted)")]
internal sealed record GeminiCredential
{
    public GeminiCredentialKind Kind { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Value { get; }

    public string Account { get; }

    /// <summary>Google Cloud project id, when the credential's source stated one (a service-account
    /// key's own <c>project_id</c> field). Empty for an OAuth login, which carries no project.</summary>
    public string Project { get; }

    /// <summary>Normalized to UTC; <see langword="null"/> when the source has no usable expiry (every
    /// service-account credential, and a Gemini CLI login whose <c>expiry_date</c> was missing or
    /// invalid).</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>Proves identity but cannot necessarily read usage - e.g. a service-account file
    /// missing required fields, or a gcloud user login. See <see cref="LimitedReason"/> for why.</summary>
    public bool UsageCapable { get; }

    public string LimitedReason { get; }

    public GeminiCredential(
        GeminiCredentialKind kind,
        string value,
        string account = "",
        string project = "",
        DateTimeOffset? expiresAtUtc = null,
        bool usageCapable = true,
        string limitedReason = "")
    {
        Kind = kind;
        Value = value ?? string.Empty;
        Account = account ?? string.Empty;
        Project = project ?? string.Empty;
        ExpiresAtUtc = expiresAtUtc;
        UsageCapable = usageCapable;
        LimitedReason = limitedReason ?? string.Empty;
    }

    /// <summary>
    /// Overrides the compiler-generated record <c>ToString</c> (which would otherwise print every
    /// property, including <see cref="Value"/>) with a fixed string that never depends on any field
    /// value, so this type is safe to interpolate into a log line or an assertion failure message no
    /// matter what untrusted content the credential file carried.
    /// </summary>
    public override string ToString() => "GeminiCredential { <redacted> }";
}
