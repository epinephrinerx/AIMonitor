using System.Text.Json;
using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Pure parsing of Codex CLI's auth JSON, with no file or network I/O of its own — that is
/// <see cref="CodexOAuthFileReader"/>'s job. Kept separate so the parsing rules (claim extraction,
/// expiry normalization, token validation) are testable without touching a filesystem at all. Every
/// expected failure (malformed content, missing/blank/unsafe token, expired token) is reported
/// through <see cref="CodexOAuthReadResult"/> rather than thrown.
///
/// Only the ChatGPT OAuth shape (<c>tokens.access_token</c>) is discovered here. Codex CLI can also
/// store a plain API key at the auth file's top level (<c>OPENAI_API_KEY</c>); this slice treats that
/// possibility as an injected candidate the caller supplies directly
/// (<see cref="OpenAiApiKeyCandidate.CliKey"/>) rather than a second output of this parser, so there
/// is exactly one way to represent "a Codex OAuth login was discovered".
/// </summary>
public static class CodexOAuthParser
{
    /// <summary>Values above this are epoch milliseconds rather than seconds — matches the baseline's
    /// "past ~year 2286 in seconds is really milliseconds" heuristic, applied here to the JWT
    /// <c>exp</c> claim the same way the baseline applies it to Claude's <c>expiresAt</c> field.</summary>
    private const long MillisecondEpochThreshold = 10_000_000_000;

    private static readonly string[] AccountClaimNames = ["email", "preferred_username", "name", "sub"];

    /// <summary>
    /// Parses already-decoded JSON text read from <paramref name="path"/> (used only to populate the
    /// result, never re-read here) into a <see cref="CodexOAuthReadResult"/>.
    /// </summary>
    public static CodexOAuthReadResult Parse(string json, string path, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(clock);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return CodexOAuthReadResult.Invalid(path);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CodexOAuthReadResult.Invalid(path);
            }

            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            {
                return CodexOAuthReadResult.Invalid(path);
            }

            if (!tokens.TryGetProperty("access_token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String)
            {
                return CodexOAuthReadResult.Invalid(path);
            }

            var accessToken = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(accessToken) || ContainsControlCharacter(accessToken))
            {
                return CodexOAuthReadResult.Invalid(path);
            }

            var tokensAccountId = JwtClaimsReader.GetString(tokens, "account_id") ?? string.Empty;

            var idTokenClaims = JwtClaimsReader.TryGetClaims(JwtClaimsReader.GetString(tokens, "id_token"));
            var account = AccountFromClaims(idTokenClaims);
            if (string.IsNullOrEmpty(account))
            {
                account = tokensAccountId;
            }

            var accessClaims = JwtClaimsReader.TryGetClaims(accessToken);
            var accountId = tokensAccountId;
            if (string.IsNullOrEmpty(accountId))
            {
                accountId = ChatGptAccountIdClaim(accessClaims) ?? string.Empty;
            }

            var expiresAtUtc = ParseExpiryClaim(accessClaims);
            var credential = new CodexOAuthCredential(accessToken, expiresAtUtc, account, accountId);

            return expiresAtUtc is { } expiry && expiry <= clock.UtcNow
                ? CodexOAuthReadResult.Expired(credential, path)
                : CodexOAuthReadResult.Found(credential, path);
        }
    }

    /// <summary>An access token containing a control character (e.g. a stray newline or NUL) is
    /// never a legitimate bearer token and is rejected rather than forwarded into an
    /// <c>Authorization</c> header.</summary>
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

    /// <summary>Mirrors the baseline's <c>account_from_claims</c>: the first non-blank of these
    /// claims, in this order, or empty when none are present as a string.</summary>
    private static string AccountFromClaims(JsonElement? claims)
    {
        foreach (var name in AccountClaimNames)
        {
            var value = JwtClaimsReader.GetString(claims, name);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>Codex's access token carries the ChatGPT account ID one level deep, under the
    /// <c>https://api.openai.com/auth</c> claim — used only as a fallback when <c>tokens.account_id</c>
    /// itself is absent.</summary>
    private static string? ChatGptAccountIdClaim(JsonElement? accessClaims)
    {
        if (accessClaims is not { } claims
            || !claims.TryGetProperty("https://api.openai.com/auth", out var auth)
            || auth.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JwtClaimsReader.GetString(auth, "chatgpt_account_id");
    }

    /// <summary>
    /// Accepts numeric expiry (the JWT <c>exp</c> claim) in epoch seconds or milliseconds, normalized
    /// to UTC. Missing, non-numeric, non-finite, nonpositive, or out-of-range values become
    /// <see langword="null"/> ("unknown") rather than an invented expiry. Integral values are parsed
    /// as <see cref="long"/> so whole-second/millisecond epochs never suffer any rounding at all; a
    /// genuinely fractional value is parsed as <see cref="decimal"/> and converted directly to ticks
    /// (100ns), preserving <see cref="DateTimeOffset"/>'s full tick precision instead of rounding
    /// through milliseconds.
    /// </summary>
    private static DateTimeOffset? ParseExpiryClaim(JsonElement? accessClaims)
    {
        var value = JwtClaimsReader.GetNumber(accessClaims, "exp");
        if (value is not { } number)
        {
            return null;
        }

        if (number.TryGetInt64(out var rawInteger))
        {
            if (rawInteger <= 0)
            {
                return null;
            }

            try
            {
                var millis = rawInteger > MillisecondEpochThreshold ? rawInteger : checked(rawInteger * 1000);
                return DateTimeOffset.FromUnixTimeMilliseconds(millis).ToUniversalTime();
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        // Fractional values (a decimal point present in the JSON literal) are parsed as decimal
        // rather than double and converted straight to ticks (100ns), never through an intermediate
        // millisecond rounding step, so sub-millisecond precision in the source value is preserved
        // rather than lost to double's binary rounding or a millisecond-granularity round-trip.
        if (!number.TryGetDecimal(out var raw) || raw <= 0)
        {
            return null;
        }

        decimal roundedTicks;
        try
        {
            var ticksFromEpoch = raw > MillisecondEpochThreshold
                ? raw * TimeSpan.TicksPerMillisecond
                : raw * TimeSpan.TicksPerSecond;
            roundedTicks = decimal.Round(ticksFromEpoch, 0, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (roundedTicks > long.MaxValue || roundedTicks < long.MinValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.UnixEpoch.AddTicks((long)roundedTicks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
