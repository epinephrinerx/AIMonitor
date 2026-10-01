using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Parses a Unix epoch value shared by three unrelated payloads in this provider: a Codex login's JWT
/// <c>exp</c> claim, and a Codex quota window's <c>resetsAt</c>. Matches the Python baseline's
/// "accept seconds or milliseconds" heuristic everywhere, rather than assuming JWT claims are always
/// whole seconds - lenient parsing here costs nothing and keeps every epoch value in this provider
/// behaving identically.
/// </summary>
internal static class OpenAiEpoch
{
    /// <summary>Values past this are epoch milliseconds rather than seconds (a value this large in
    /// seconds would be the year 2286).</summary>
    private const double MillisecondThreshold = 10_000_000_000;

    /// <summary>Missing, non-finite, or non-positive values are unknown rather than invented.</summary>
    public static DateTimeOffset? Parse(double? value)
    {
        if (value is not double raw || !double.IsFinite(raw) || raw <= 0)
        {
            return null;
        }

        var seconds = raw > MillisecondThreshold ? raw / 1000.0 : raw;
        try
        {
            return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Reads a JSON number, treating anything else (including a JSON string, bool, or a
    /// missing/null property) as absent rather than coerced.</summary>
    public static DateTimeOffset? Parse(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? Parse(value)
            : null;
}
