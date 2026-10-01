using System.Diagnostics;

namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Codex's ChatGPT OAuth login, read from its auth file. <see cref="AccessToken"/> is a bearer
/// secret: it must never appear in <see cref="ToString"/>, an exception message, a log line, or a
/// debugger tooltip, so every one of those surfaces is redacted here rather than left to the default
/// record-generated representation. <see cref="Account"/> and <see cref="AccountId"/> are derived
/// from unverified JWT claims (display only, no signature check — see
/// <see cref="JwtClaimsReader"/>), so they are untrusted metadata too; callers must not treat them as
/// authenticated facts.
/// </summary>
[DebuggerDisplay("CodexOAuthCredential (redacted)")]
public sealed record CodexOAuthCredential
{
    private const string NoAccountIdReason = "Codex login has no account ID. Sign in to Codex again.";

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string AccessToken { get; }

    /// <summary>Normalized to UTC; <see langword="null"/> when the source had no usable expiry.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>Display name for the signed-in account, or empty when none of the known claims were
    /// present.</summary>
    public string Account { get; }

    /// <summary>Untrusted account identifier used only to decide whether this login can read usage
    /// (<see cref="IsUsageCapable"/>) — never sent anywhere or treated as an authenticated fact.</summary>
    public string AccountId { get; }

    /// <summary>A Codex login without an account ID cannot read quota, matching the baseline: it is
    /// still a real, current login (so it must not be silently replaced by a saved/environment/CLI
    /// key), just one that cannot be used yet.</summary>
    public bool IsUsageCapable => !string.IsNullOrEmpty(AccountId);

    public string LimitedReason => IsUsageCapable ? string.Empty : NoAccountIdReason;

    public CodexOAuthCredential(string accessToken, DateTimeOffset? expiresAtUtc, string account, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        AccessToken = accessToken;
        ExpiresAtUtc = expiresAtUtc;
        Account = account ?? string.Empty;
        AccountId = accountId ?? string.Empty;
    }

    /// <summary>
    /// Overrides the compiler-generated record <c>ToString</c> (which would otherwise print every
    /// property, including <see cref="AccessToken"/>) with a fixed string that never depends on any
    /// field value, so this type is safe to interpolate into a log line or an assertion failure
    /// message no matter what untrusted content the auth file carried.
    /// </summary>
    public override string ToString() => "CodexOAuthCredential { <redacted> }";
}
