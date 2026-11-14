using System.Diagnostics;

namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// One non-OAuth OpenAI API key candidate the caller injects into selection — the saved Admin key,
/// an environment variable's value, or Codex CLI's own stored API key. This phase does not implement
/// DPAPI-backed saved-secret storage or environment/CLI discovery; a future composition root supplies
/// whichever raw value it resolved through <see cref="SavedAdmin"/>, <see cref="Environment"/>, or
/// <see cref="CliKey"/>, and this type only shapes/validates it. <see cref="Key"/> is a bearer secret
/// and must never appear in <see cref="ToString"/>, an exception message, or a log line.
/// </summary>
[DebuggerDisplay("OpenAiApiKeyCandidate (redacted)")]
public sealed record OpenAiApiKeyCandidate
{
    private const string AdminKeyPrefix = "sk-admin-";

    /// <summary>Matches the baseline: an ordinary project key (<c>sk-...</c>) is rejected by the
    /// organization Usage/Costs endpoints, which require an Admin key created by an organization
    /// owner.</summary>
    public const string NotAdminKeyReason =
        "API spend requires an organization Admin key (sk-admin-…). Sign in to Codex with ChatGPT to monitor Codex quota instead.";

    public string SourceId { get; }

    public string SourceLabel { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Key { get; }

    /// <summary>Display text for where this key came from, e.g. <c>"saved in this app"</c> or
    /// <c>"from $OPENAI_ADMIN_KEY"</c>. Never the key value itself.</summary>
    public string Account { get; }

    /// <summary>Only an exact <c>sk-admin-</c> prefix can read organization spend; this is a shape
    /// check only — no network call validates the key actually works (baseline parity: "validate key
    /// shape/metadata only, without network or invented claims").</summary>
    public bool IsUsageCapable => Key.StartsWith(AdminKeyPrefix, StringComparison.Ordinal);

    public string LimitedReason => IsUsageCapable ? string.Empty : NotAdminKeyReason;

    private OpenAiApiKeyCandidate(string sourceId, string sourceLabel, string key, string account)
    {
        SourceId = sourceId;
        SourceLabel = sourceLabel;
        Key = key;
        Account = account;
    }

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="rawKey"/> is blank or contains a control
    /// character — an absent or unsafe candidate is excluded from selection entirely rather than
    /// reported as a malformed-but-present one, matching the baseline's "empty key means no
    /// usage-capable login" and this codebase's treatment of a control-character-bearing token as
    /// never legitimate.
    /// </summary>
    private static OpenAiApiKeyCandidate? TryCreate(string sourceId, string sourceLabel, string? rawKey, string account)
    {
        var trimmed = rawKey?.Trim();
        if (string.IsNullOrEmpty(trimmed) || ContainsControlCharacter(trimmed))
        {
            return null;
        }

        return new OpenAiApiKeyCandidate(sourceId, sourceLabel, trimmed, account);
    }

    /// <summary>The Admin key a user saved in this app's settings. Fixed source identity matches the
    /// baseline's <c>manual</c> source.</summary>
    public static OpenAiApiKeyCandidate? SavedAdmin(string? rawKey) =>
        TryCreate("manual", "Admin key saved in this app", rawKey, "saved in this app");

    /// <summary>A key read from an environment variable. <paramref name="variableName"/> (e.g.
    /// <c>"OPENAI_ADMIN_KEY"</c> or <c>"OPENAI_API_KEY"</c>) is display text only — the caller is
    /// responsible for choosing which variable it read and for that variable's own precedence.</summary>
    public static OpenAiApiKeyCandidate? Environment(string? rawKey, string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        return TryCreate("env", "Environment variable", rawKey, $"from ${variableName}");
    }

    /// <summary>A plain API key Codex CLI stored for itself, as opposed to a ChatGPT OAuth login.
    /// Shares the baseline's <c>codex_cli</c> source identity with <see cref="CodexOAuthParser"/>'s
    /// discovery because both represent the same underlying Codex CLI login file, just interpreted as
    /// a different credential shape.</summary>
    public static OpenAiApiKeyCandidate? CliKey(string? rawKey) =>
        TryCreate("codex_cli", "Codex CLI login (ChatGPT)", rawKey, account: string.Empty);

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString() => "OpenAiApiKeyCandidate { <redacted> }";
}
