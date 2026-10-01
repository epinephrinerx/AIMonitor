using System.Diagnostics;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Claude Code's OAuth login, read from its credentials file. <see cref="AccessToken"/> is a
/// bearer secret: it must never appear in <see cref="ToString"/>, an exception message, a log line,
/// or a debugger tooltip, so every one of those surfaces is redacted here rather than left to the
/// default record-generated representation. <see cref="SubscriptionType"/> and
/// <see cref="RateLimitTier"/> are untrusted server-supplied metadata too (a malicious/corrupted
/// credentials file could duplicate the token into either field), so the fixed display below never
/// echoes them back either.
/// </summary>
[DebuggerDisplay("ClaudeCredentials (redacted)")]
public sealed record ClaudeCredentials
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string AccessToken { get; }

    /// <summary>Normalized to UTC; <see langword="null"/> when the source had no usable expiry.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    public string? SubscriptionType { get; }

    public string? RateLimitTier { get; }

    public ClaudeCredentials(
        string accessToken,
        DateTimeOffset? expiresAtUtc,
        string? subscriptionType,
        string? rateLimitTier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        AccessToken = accessToken;
        ExpiresAtUtc = expiresAtUtc;
        SubscriptionType = subscriptionType;
        RateLimitTier = rateLimitTier;
    }

    /// <summary>
    /// Overrides the compiler-generated record <c>ToString</c> (which would otherwise print every
    /// property, including <see cref="AccessToken"/>) with a fixed string that never depends on any
    /// field value, so this type is safe to interpolate into a log line or an assertion failure
    /// message no matter what untrusted content the credentials file carried.
    /// </summary>
    public override string ToString() => "ClaudeCredentials { <redacted> }";
}
