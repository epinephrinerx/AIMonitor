using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Decodes the payload segment of a JWT for display and routing purposes only - there is no signature
/// verification here, deliberately. The payload is base64url, not encryption, and Codex CLI already
/// validated the token before writing it to its login file; this only reads metadata (an account
/// label, an account ID, an expiry) that the CLI already trusts. No authorization decision is ever
/// based on these unverified claims - the raw token string itself is what gets sent to the App
/// Server, which performs its own validation.
/// </summary>
internal static class OpenAiJwt
{
    private static readonly string[] AccountClaimNames = ["email", "preferred_username", "name", "sub"];

    /// <summary>
    /// Returns the decoded claims object, or <see langword="null"/> when <paramref name="token"/> is
    /// missing, is not the standard three-segment <c>header.payload.signature</c> shape, or the
    /// payload segment is not valid base64url JSON. Caller-owned: dispose the returned document.
    /// </summary>
    public static JsonDocument? DecodeClaims(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length == 0)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first non-blank string value among <c>email</c>, <c>preferred_username</c>,
    /// <c>name</c>, and <c>sub</c>; empty when <paramref name="claims"/> is not an object or none are
    /// present as a non-blank string.</summary>
    public static string AccountFromClaims(JsonElement claims)
    {
        if (claims.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var name in AccountClaimNames)
        {
            if (claims.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(value.GetString()))
            {
                return value.GetString()!;
            }
        }

        return string.Empty;
    }
}
