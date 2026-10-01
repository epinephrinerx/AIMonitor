using System.Globalization;
using System.Text.Json;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Pure mapping from the Codex App Server's <c>account/rateLimits/read</c> and
/// <c>account/usage/read</c> results into the provider-neutral <see cref="Meter"/>/<see cref="Stat"/>/
/// <see cref="UsageHistory"/> contract. No I/O of its own - the raw <see cref="JsonElement"/> results
/// come from <see cref="CodexAppServerLauncher"/>.
/// </summary>
internal static class CodexQuotaMapper
{
    private const double WeeklyDurationMinutes = 10080;

    /// <summary>
    /// Maps every rate-limit window across every limit group (<c>rateLimitsByLimitId</c>, plus the
    /// legacy top-level <c>rateLimits</c> block when it names a group not already present) into
    /// meters, in the App Server's own group/window order - never re-sorted by percentage, and never
    /// run through the cross-provider <see cref="MeterReadingOrder"/>, because that natural order
    /// (the whole-account "codex" group first, its primary window before its secondary) is already
    /// stable across refreshes, matching the Python baseline exactly. <see cref="Meter.Kind"/> mirrors
    /// <see cref="Meter.Key"/> here rather than carrying invented cross-window semantics.
    /// </summary>
    public static IReadOnlyList<Meter> Meters(JsonElement payload)
    {
        var groups = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("rateLimitsByLimitId", out var byLimitId)
            && byLimitId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byLimitId.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    groups[property.Name] = property.Value;
                }
            }
        }

        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("rateLimits", out var legacy)
            && legacy.ValueKind == JsonValueKind.Object)
        {
            var legacyLimitId = ReadOptionalString(legacy, "limitId");
            var key = string.IsNullOrEmpty(legacyLimitId) ? "codex" : legacyLimitId;
            groups.TryAdd(key, legacy); // first-writer-wins: rateLimitsByLimitId already covers `key`
        }

        var meters = new List<Meter>();
        foreach (var limitId in groups.Keys.OrderBy(id => id != "codex").ThenBy(id => id, StringComparer.Ordinal))
        {
            AppendGroupMeters(limitId, groups[limitId], meters);
        }

        return meters;
    }

    private static void AppendGroupMeters(string limitId, JsonElement group, List<Meter> meters)
    {
        var rawLabel = ReadOptionalString(group, "limitName");
        var label = string.IsNullOrEmpty(rawLabel) ? limitId : rawLabel;
        var groupReason = ReadOptionalString(group, "rateLimitReachedType");

        foreach (var windowKind in new[] { "primary", "secondary" })
        {
            if (!group.TryGetProperty(windowKind, out var window) || window.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var percent = ReadFinitePercent(window);
            if (percent is not double percentValue)
            {
                continue;
            }

            var title = windowKind == "primary" ? "Session" : "Secondary";
            var subtitle = "Codex quota";
            var duration = ReadPositiveFiniteNumber(window, "windowDurationMins");
            if (duration is double durationValue)
            {
                if (durationValue == WeeklyDurationMinutes)
                {
                    title = "Weekly";
                }

                subtitle = (long)durationValue % 60 == 0
                    ? $"{FormatTrimmedNumber(durationValue / 60)}-hour window"
                    : $"{FormatTrimmedNumber(durationValue)}-minute window";
            }

            if (limitId != "codex")
            {
                title = $"{label} · {title}";
            }

            var key = $"codex_{limitId}_{windowKind}";
            var clampedPercent = Math.Min(100.0, percentValue);
            var reason = ReadOptionalString(window, "rateLimitReachedType") ?? groupReason;
            var severity = !string.IsNullOrEmpty(reason) || clampedPercent >= 90
                ? Severity.Critical
                : clampedPercent >= 75
                    ? Severity.High
                    : Severity.Normal;

            var resetsAt = window.TryGetProperty("resetsAt", out var resetsAtElement)
                ? OpenAiEpoch.Parse(resetsAtElement)
                : null;

            meters.Add(new Meter(
                kind: key,
                key: key,
                title: title,
                subtitle: subtitle,
                percent: clampedPercent,
                serverSeverity: severity,
                resetsAt: resetsAt,
                lockedReason: string.IsNullOrEmpty(reason) ? null : reason.Replace('_', ' ')));
        }
    }

    /// <summary>
    /// Maps <c>account/usage/read</c> into lifetime/peak-daily stats (when reported) and, only for the
    /// "Total tokens" metric, a single-series daily history built strictly from the days the server
    /// actually reported - a day with no bucket is left out of the range entirely rather than shown as
    /// zero, and a day the server reports outside <paramref name="today"/>'s local window is dropped.
    /// The server exposes total tokens only: no output-token, model/project, or dollar breakdown, so
    /// every other metric returns no history at all rather than an invented one.
    /// </summary>
    public static (UsageHistory? History, IReadOnlyList<Stat> Stats) History(
        JsonElement payload, int days, string metric, DateOnly today)
    {
        var stats = new List<Stat>();

        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("summary", out var summary)
            && summary.ValueKind == JsonValueKind.Object)
        {
            AppendSummaryStat(summary, "lifetimeTokens", "Lifetime tokens", stats);
            AppendSummaryStat(summary, "peakDailyTokens", "Peak daily tokens", stats);
        }

        // Whitespace-only is treated the same as blank - UsageHistory's constructor requires a
        // non-blank metric, and ProviderSnapshotRequest.Metric only guarantees non-null, not non-blank.
        var effectiveMetric = string.IsNullOrWhiteSpace(metric) ? UsageMetric.TotalTokens : metric;
        if (effectiveMetric != UsageMetric.TotalTokens
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("dailyUsageBuckets", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return (null, stats);
        }

        var clampedDays = Math.Clamp(days, 1, 90);
        var start = today.AddDays(-(clampedDays - 1));

        var values = new SortedDictionary<DateOnly, long>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("startDate", out var startDateElement)
                || startDateElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(
                    startDateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day))
            {
                continue;
            }

            if (day < start || day > today)
            {
                continue;
            }

            if (!row.TryGetProperty("tokens", out var tokensElement)
                || tokensElement.ValueKind != JsonValueKind.Number
                || !tokensElement.TryGetInt64(out var tokens)
                || tokens < 0)
            {
                continue;
            }

            values[day] = tokens;
        }

        if (values.Count == 0)
        {
            return (null, stats);
        }

        var buckets = values
            .Select(pair => new UsageHistoryBucket(pair.Key, new Dictionary<string, double> { ["Codex"] = pair.Value }))
            .ToList();
        var total = values.Values.Sum();

        stats.Insert(0, new Stat("Reported tokens in range", OpenAiNumberFormatting.FormatCompactNumber(total), "Available daily buckets"));

        var history = new UsageHistory(buckets: buckets, series: ["Codex"], days: clampedDays, metric: effectiveMetric);
        return (history, stats);
    }

    private static void AppendSummaryStat(JsonElement summary, string propertyName, string label, List<Stat> stats)
    {
        if (summary.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var value)
            && value >= 0)
        {
            stats.Add(new Stat(label, OpenAiNumberFormatting.FormatCompactNumber(value), "Reported by Codex"));
        }
    }

    private static double? ReadFinitePercent(JsonElement window)
    {
        if (!window.TryGetProperty("usedPercent", out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            return null;
        }

        return value;
    }

    private static double? ReadPositiveFiniteNumber(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value <= 0)
        {
            return null;
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A whole number prints without a decimal point ("5"); anything else prints with the
    /// minimum digits needed ("1.5"), mirroring Python's <c>:g</c> format used for window durations.</summary>
    private static string FormatTrimmedNumber(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
}
