using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Decodes a JWT payload for display only, matching the Python baseline: there is no signature
/// verification here. The payload is base64url, not encryption, and this reads it purely to show
/// which account is signed in — a forged or self-signed token decodes exactly as well as a
/// legitimate one, which is acceptable only because nothing in this codebase treats the result as an
/// authenticated fact; it is shown or silently ignored, never used to authorize anything.
/// </summary>
internal static class JwtClaimsReader
{
    /// <summary>Real Codex JWTs are a few KB at most; this bounds a pathological token embedded in an
    /// otherwise-valid auth file independent of the file reader's own overall cap.</summary>
    private const int MaxTokenLength = 64 * 1024;

    /// <summary>
    /// Returns the decoded claims object, or <see langword="null"/> when <paramref name="token"/> is
    /// missing, is not a three-segment JWT, has an unparsable base64url payload segment, or decodes
    /// to JSON that is not an object. Never throws.
    /// </summary>
    public static JsonElement? TryGetClaims(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        var segments = token.Split('.');
        if (segments.Length != 3)
        {
            return null;
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(segments[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadBytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns the string value of <paramref name="propertyName"/> in <paramref name="claims"/>,
    /// or <see langword="null"/> when absent, non-string, or <paramref name="claims"/> itself is
    /// <see langword="null"/>.</summary>
    public static string? GetString(JsonElement? claims, string propertyName) =>
        claims is { } obj && obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Returns the numeric value of <paramref name="propertyName"/> in <paramref name="claims"/>,
    /// or <see langword="null"/> when absent, non-numeric, or <paramref name="claims"/> itself is
    /// <see langword="null"/>.</summary>
    public static JsonElement? GetNumber(JsonElement? claims, string propertyName) =>
        claims is { } obj && obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value
            : null;

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder != 0)
        {
            padded += new string('=', 4 - remainder);
        }

        return Convert.FromBase64String(padded);
    }
}
