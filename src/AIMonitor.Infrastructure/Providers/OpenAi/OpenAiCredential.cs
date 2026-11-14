using System.Diagnostics;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>The kind of login a credential source yielded, mirroring the Python baseline's
/// OAUTH/API_KEY vocabulary. Only a <see cref="OAuth"/> credential from Codex CLI's login can ever
/// take priority over a saved/environment/CLI key (PAR-008).</summary>
public enum OpenAiCredentialKind
{
    OAuth,
    ApiKey,
}

/// <summary>
/// One usable OpenAI-side login plus where it came from. <see cref="Value"/> is a bearer secret (an
/// API key or a Codex ChatGPT OAuth access token): it must never appear in <see cref="ToString"/>, an
/// exception message, a log line, or a debugger tooltip, so every one of those surfaces is redacted
/// here rather than left to the default record-generated representation. This type is
/// Infrastructure-internal and never travels into a <see cref="Domain.ProviderSnapshot"/> or
/// <see cref="Domain.DetectionInfo"/> - those carry only the account/state metadata a caller needs.
/// </summary>
[DebuggerDisplay("OpenAiCredential (redacted)")]
public sealed record OpenAiCredential
{
    public OpenAiCredentialKind Kind { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Value { get; }

    public string Account { get; }

    /// <summary>Codex account identifier the App Server login call needs. Empty for API-key-kind
    /// credentials, and for an OAuth login that has none (see <see cref="UsageCapable"/>).</summary>
    public string AccountId { get; }

    /// <summary>Normalized to UTC; <see langword="null"/> when the source has no usable expiry (every
    /// API-key-kind credential in this provider, and an OAuth login whose token could not be decoded).</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>Proves identity but cannot necessarily read usage - e.g. an ordinary (non-Admin)
    /// project key, or a Codex login with no account ID. See <see cref="LimitedReason"/> for why.</summary>
    public bool UsageCapable { get; }

    public string LimitedReason { get; }

    public OpenAiCredential(
        OpenAiCredentialKind kind,
        string value,
        string account = "",
        string accountId = "",
        DateTimeOffset? expiresAtUtc = null,
        bool usageCapable = true,
        string limitedReason = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Kind = kind;
        Value = value;
        Account = account ?? string.Empty;
        AccountId = accountId ?? string.Empty;
        ExpiresAtUtc = expiresAtUtc;
        UsageCapable = usageCapable;
        LimitedReason = limitedReason ?? string.Empty;
    }

    /// <summary>
    /// Overrides the compiler-generated record <c>ToString</c> (which would otherwise print every
    /// property, including <see cref="Value"/>) with a fixed string that never depends on any field
    /// value, so this type is safe to interpolate into a log line or an assertion failure message no
    /// matter what untrusted content the login file carried.
    /// </summary>
    public override string ToString() => "OpenAiCredential { <redacted> }";
}
