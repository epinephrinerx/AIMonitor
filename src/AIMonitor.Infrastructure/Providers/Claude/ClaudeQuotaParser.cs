using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Parses a Claude OAuth usage payload (the shape returned by `/api/oauth/usage`) into the
/// provider-neutral <see cref="Meter"/> contract. Performs no I/O beyond reading the caller-supplied
/// JSON — no HTTP, credential, or filesystem access happens here.
/// </summary>
public static class ClaudeQuotaParser
{
    /// <summary>Known single-window kinds and their fixed (title, subtitle) pair.</summary>
    private static readonly IReadOnlyDictionary<string, (string Title, string Subtitle)> KnownTitles =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["session"] = ("Session", "5-hour window"),
            ["weekly_all"] = ("Weekly", "All models"),
            ["weekly_opus"] = ("Weekly", "Opus only"),
            ["weekly_sonnet"] = ("Weekly", "Sonnet only"),
            ["weekly_oauth_apps"] = ("Weekly", "API apps"),
        };

    /// <summary>Legacy top-level blocks, in the order they must be read when `limits` is absent.</summary>
    private static readonly (string Kind, string BlockName)[] LegacyBlocksInOrder =
    [
        ("session", "five_hour"),
        ("weekly_all", "seven_day"),
        ("weekly_opus", "seven_day_opus"),
        ("weekly_sonnet", "seven_day_sonnet"),
        ("weekly_oauth_apps", "seven_day_oauth_apps"),
    ];

    private static readonly IReadOnlyDictionary<string, string> LegacyBlockForKind =
        LegacyBlocksInOrder.ToDictionary(pair => pair.Kind, pair => pair.BlockName, StringComparer.Ordinal);

    public static IReadOnlyList<Meter> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = ParseDocument(() => JsonDocument.Parse(json));
        return ParseDocument(document);
    }

    public static IReadOnlyList<Meter> Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = ParseDocument(() => JsonDocument.Parse(utf8Json));
        return ParseDocument(document);
    }

    public static IReadOnlyList<Meter> Parse(Stream utf8JsonStream)
    {
        ArgumentNullException.ThrowIfNull(utf8JsonStream);

        using var document = ParseDocument(() => JsonDocument.Parse(utf8JsonStream));
        return ParseDocument(document);
    }

    private static JsonDocument ParseDocument(Func<JsonDocument> parse)
    {
        try
        {
            return parse();
        }
        catch (JsonException)
        {
            // The underlying JsonException may quote fragments of the offending payload text, so it
            // is never attached as an inner exception — only this fixed message is safe to log.
            throw new ClaudeQuotaParseException("Claude quota payload is not valid JSON.");
        }
    }

    private static IReadOnlyList<Meter> ParseDocument(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ClaudeQuotaParseException("Claude quota payload root must be a JSON object.");
        }

        var meters = new List<Meter>();

        if (root.TryGetProperty("limits", out var limitsElement)
            && limitsElement.ValueKind == JsonValueKind.Array
            && limitsElement.GetArrayLength() > 0)
        {
            ParseCurrentLimits(root, limitsElement, meters);
        }
        else
        {
            ParseLegacyBlocks(root, meters);
        }

        IReadOnlyList<Meter> sorted;
        try
        {
            sorted = MeterReadingOrder.Sort(meters);
        }
        catch (ArgumentException)
        {
            // Domain's duplicate-key message embeds the colliding key, which may carry an
            // untrusted scope display name, so it is never attached as an inner exception.
            throw new ClaudeQuotaParseException(
                "Claude quota payload contains duplicate meter identities.");
        }

        // Guard against the reading order's array leaking mutation through an IList<Meter> cast.
        return new ReadOnlyCollection<Meter>(sorted.ToList());
    }

    private static void ParseCurrentLimits(JsonElement root, JsonElement limits, List<Meter> meters)
    {
        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                // Older/newer servers may interleave non-object members; skip rather than fail.
                continue;
            }

            var kind = ReadKind(entry);
            var (percent, lockedReason) = ResolvePercentAndLockedReason(root, entry, kind);
            var (title, subtitle, key) = ResolveTitleSubtitleAndKey(kind, entry);
            var group = ReadOptionalString(entry, "group");
            var severity = SeverityLabels.Parse(ReadOptionalString(entry, "severity"));
            var resetsAt = ParseTimestamp(ReadOptionalString(entry, "resets_at"));

            meters.Add(CreateMeter(kind, key, title, subtitle, percent, severity, resetsAt, lockedReason, group));
        }
    }

    private static void ParseLegacyBlocks(JsonElement root, List<Meter> meters)
    {
        foreach (var (kind, blockName) in LegacyBlocksInOrder)
        {
            if (!root.TryGetProperty(blockName, out var block) || block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var percent = ReadNullableNumber(block, "utilization") ?? 0.0;
            var lockedReason = ReadOptionalString(block, "locked_reason");
            var resetsAt = ParseTimestamp(ReadOptionalString(block, "resets_at"));
            var (title, subtitle) = KnownTitles[kind];
            var group = kind == "session" ? "session" : "weekly";

            meters.Add(CreateMeter(kind, kind, title, subtitle, percent, Severity.Normal, resetsAt, lockedReason, group));
        }
    }

    private static (double Percent, string? LockedReason) ResolvePercentAndLockedReason(
        JsonElement root, JsonElement entry, string kind)
    {
        var percent = ReadNullableNumber(entry, "percent");
        string? lockedReason = null;

        if (LegacyBlockForKind.TryGetValue(kind, out var blockName)
            && root.TryGetProperty(blockName, out var legacy)
            && legacy.ValueKind == JsonValueKind.Object)
        {
            // The legacy block carries an unrounded utilization that overrides the current one.
            var legacyUtilization = ReadNullableNumber(legacy, "utilization");
            if (legacyUtilization is double overridePercent)
            {
                percent = overridePercent;
            }

            lockedReason = ReadOptionalString(legacy, "locked_reason");
        }

        return (percent ?? 0.0, lockedReason);
    }

    private static (string Title, string Subtitle, string Key) ResolveTitleSubtitleAndKey(string kind, JsonElement entry)
    {
        if (KnownTitles.TryGetValue(kind, out var known))
        {
            return (known.Title, known.Subtitle, kind);
        }

        if (kind == "weekly_scoped")
        {
            var (scopeType, identity) = ResolveScopeIdentity(entry);
            return ("Weekly", $"{identity} only", $"weekly_scoped:{scopeType}:{identity}");
        }

        // Unknown kind from a newer server: render it rather than dropping it.
        var pretty = Prettify(kind);
        var groupHint = kind.StartsWith("weekly", StringComparison.Ordinal) ? "Weekly" : pretty;
        return (groupHint, pretty, kind);
    }

    /// <summary>
    /// Resolves the scope type alongside the display identity so a model and a surface sharing one
    /// display name still get distinct stable keys (<c>weekly_scoped:model:&lt;name&gt;</c> vs
    /// <c>weekly_scoped:surface:&lt;name&gt;</c>); the fallback path is its own deterministic type.
    /// </summary>
    private static (string ScopeType, string Identity) ResolveScopeIdentity(JsonElement entry)
    {
        if (entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object)
        {
            var modelName = ReadNestedDisplayName(scope, "model");
            if (!string.IsNullOrEmpty(modelName))
            {
                return ("model", modelName);
            }

            var surfaceName = ReadNestedDisplayName(scope, "surface");
            if (!string.IsNullOrEmpty(surfaceName))
            {
                return ("surface", surfaceName);
            }
        }

        return ("fallback", "Scoped");
    }

    private static string? ReadNestedDisplayName(JsonElement scope, string propertyName)
    {
        if (scope.TryGetProperty(propertyName, out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty("display_name", out var displayName)
            && displayName.ValueKind == JsonValueKind.String)
        {
            return displayName.GetString();
        }

        return null;
    }

    private static string Prettify(string kind)
    {
        var replaced = kind.Replace('_', ' ').Trim();
        if (replaced.Length == 0)
        {
            return replaced;
        }

        var lower = replaced.ToLowerInvariant();
        return string.Concat(char.ToUpperInvariant(lower[0]), lower[1..]);
    }

    private static string ReadKind(JsonElement entry)
    {
        if (entry.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String)
        {
            var value = kindElement.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return "unknown";
    }

    private static string? ReadOptionalString(JsonElement entry, string propertyName)
    {
        if (entry.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Reads a JSON number, treating a missing/null property as "no value" rather than zero so the
    /// caller can decide the fallback. Any other JSON type is structurally invalid input, not a
    /// value to coerce, so it fails predictably instead of silently becoming zero.
    /// </summary>
    private static double? ReadNullableNumber(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        // propertyName is always one of our own fixed protocol field names, never payload content.
        throw new ClaudeQuotaParseException(
            $"Claude quota payload has a non-numeric '{propertyName}' value.");
    }

    /// <summary>
    /// Accepts UTC `Z`, an explicit offset, or a naive ISO value (treated as UTC, matching the
    /// Python baseline). Every valid result is normalized to offset zero (matching Python's
    /// `astimezone(UTC)`) while the represented instant is preserved. Missing, empty, or
    /// unreadable input becomes <see langword="null"/> — never the local machine time zone.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static Meter CreateMeter(
        string kind,
        string key,
        string title,
        string subtitle,
        double percent,
        Severity severity,
        DateTimeOffset? resetsAt,
        string? lockedReason,
        string? group)
    {
        try
        {
            return new Meter(
                kind,
                key,
                title,
                subtitle,
                percent,
                severity,
                resetsAt,
                detail: string.Empty,
                lockedReason: lockedReason,
                group: group);
        }
        catch (ArgumentException)
        {
            // Domain's guard messages may echo the invalid kind/key or the out-of-range value
            // itself, so neither is interpolated here nor attached as an inner exception.
            throw new ClaudeQuotaParseException("Claude quota entry has an invalid value.");
        }
    }
}
