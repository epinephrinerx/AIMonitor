using System.Text.Json;
using AIMonitor.Application.Time;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Pure parsing of Claude Code's credentials JSON, with no file or network I/O of its own — that is
/// <see cref="ClaudeCredentialFileReader"/>'s job. Kept separate so the parsing rules (expiry
/// normalization, token validation) are testable without touching a filesystem at all. Every
/// expected failure (malformed content, missing/blank/unsafe token, expired token) is reported
/// through <see cref="ClaudeCredentialReadResult"/> rather than thrown.
/// </summary>
public static class ClaudeCredentialReader
{
    /// <summary>Values above this are epoch milliseconds rather than seconds — matches the Python
    /// baseline's "past ~year 2286 in seconds is really milliseconds" heuristic.</summary>
    private const long MillisecondEpochThreshold = 10_000_000_000;

    /// <summary>
    /// Parses already-decoded JSON text read from <paramref name="path"/> (used only to populate the
    /// result, never re-read here) into a <see cref="ClaudeCredentialReadResult"/>.
    /// </summary>
    public static ClaudeCredentialReadResult Parse(string json, string path, IClock clock)
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
            return ClaudeCredentialReadResult.Invalid(path);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ClaudeCredentialReadResult.Invalid(path);
            }

            if (!root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
            {
                return ClaudeCredentialReadResult.Invalid(path);
            }

            if (!oauth.TryGetProperty("accessToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String)
            {
                return ClaudeCredentialReadResult.Invalid(path);
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token) || ContainsControlCharacter(token))
            {
                return ClaudeCredentialReadResult.Invalid(path);
            }

            var expiresAtUtc = ParseExpiry(oauth);
            var credentials = new ClaudeCredentials(
                token,
                expiresAtUtc,
                ReadOptionalString(oauth, "subscriptionType"),
                ReadOptionalString(oauth, "rateLimitTier"));

            return expiresAtUtc is DateTimeOffset expiry && expiry <= clock.UtcNow
                ? ClaudeCredentialReadResult.Expired(credentials, path)
                : ClaudeCredentialReadResult.Found(credentials, path);
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

    /// <summary>
    /// Accepts numeric expiry in epoch seconds or milliseconds, normalized to UTC. Missing,
    /// non-numeric, non-finite, nonpositive, or out-of-range values become <see langword="null"/>
    /// ("unknown") rather than an invented expiry. Integral values are parsed as <see cref="long"/>
    /// so whole-second/millisecond epochs never suffer any rounding at all; a genuinely fractional
    /// value is parsed as <see cref="decimal"/> and converted directly to ticks (100ns), preserving
    /// <see cref="DateTimeOffset"/>'s full tick precision instead of rounding through milliseconds.
    /// </summary>
    private static DateTimeOffset? ParseExpiry(JsonElement oauth)
    {
        if (!oauth.TryGetProperty("expiresAt", out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out var rawInteger))
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
        if (!value.TryGetDecimal(out var raw) || raw <= 0)
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

    private static string? ReadOptionalString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
