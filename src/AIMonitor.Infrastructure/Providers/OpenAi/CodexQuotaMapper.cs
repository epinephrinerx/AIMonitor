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
        var groups = ExtractGroups(payload);
        var meters = new List<Meter>();
        foreach (var limitId in groups.Keys.OrderBy(id => id != "codex").ThenBy(id => id, StringComparer.Ordinal))
        {
            AppendGroupMeters(limitId, groups[limitId], meters);
        }

        return meters;
    }

    /// <summary>
    /// Maps ChatGPT rate-limit metadata (plan type, workspace credits balance or explicit unlimited
    /// status, and reached-limit reason) from <c>account/rateLimits/read</c> into provider-neutral
    /// <see cref="Stat"/> tiles. When multiple metered buckets exist, stat labels are grouped with
    /// the bucket's display name or identifier (e.g. "Codex · Plan"). Absent, null, or malformed data
    /// remains absent, valid numeric zero is preserved, and currency values are never invented.
    /// </summary>
    public static IReadOnlyList<Stat> RateLimitStats(JsonElement payload)
    {
        var groups = ExtractGroups(payload);
        var stats = new List<Stat>();

        if (groups.Count > 0)
        {
            var isMultiBucket = groups.Count > 1;
            foreach (var limitId in groups.Keys.OrderBy(id => id != "codex").ThenBy(id => id, StringComparer.Ordinal))
            {
                AppendGroupStats(limitId, groups[limitId], isMultiBucket, stats);
            }
        }

        AppendResetStat(payload, stats);

        return stats;
    }

    private static Dictionary<string, JsonElement> ExtractGroups(JsonElement payload)
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

        return groups;
    }

    private static void AppendGroupStats(
        string limitId, JsonElement group, bool isMultiBucket, List<Stat> stats)
    {
        var groupLabel = GetGroupLabel(limitId, group);

        // 1) Plan type
        var rawPlan = ReadOptionalString(group, "planType");
        if (!string.IsNullOrWhiteSpace(rawPlan))
        {
            var formattedPlan = FormatPlanType(rawPlan);
            if (!string.IsNullOrEmpty(formattedPlan))
            {
                var label = isMultiBucket ? $"{groupLabel} · Plan" : "Plan";
                stats.Add(new Stat(label, formattedPlan, "ChatGPT plan"));
            }
        }

        // 2) Credits (balance or explicit unlimited status)
        var creditsValue = ReadCreditsValue(group);
        if (!string.IsNullOrWhiteSpace(creditsValue))
        {
            var label = isMultiBucket ? $"{groupLabel} · Credits" : "Credits";
            stats.Add(new Stat(label, creditsValue, "Workspace credits"));
        }

        // 3) Reached-limit reason
        var reason = ReadOptionalString(group, "rateLimitReachedType");
        if (string.IsNullOrWhiteSpace(reason))
        {
            foreach (var windowKind in new[] { "primary", "secondary" })
            {
                if (group.TryGetProperty(windowKind, out var window) && window.ValueKind == JsonValueKind.Object)
                {
                    var windowReason = ReadOptionalString(window, "rateLimitReachedType");
                    if (!string.IsNullOrWhiteSpace(windowReason))
                    {
                        reason = windowReason;
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            var formattedReason = FormatReachedType(reason);
            if (!string.IsNullOrEmpty(formattedReason))
            {
                var label = isMultiBucket ? $"{groupLabel} · Limit reason" : "Limit reason";
                stats.Add(new Stat(label, formattedReason, "Reported by Codex"));
            }
        }
    }

    private static string GetGroupLabel(string limitId, JsonElement group)
    {
        var rawLabel = ReadOptionalString(group, "limitName");
        if (!string.IsNullOrWhiteSpace(rawLabel))
        {
            return rawLabel.Trim();
        }

        return string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : limitId;
    }

    private static string? ReadCreditsValue(JsonElement group)
    {
        if (!group.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (credits.TryGetProperty("hasCredits", out var hasCreditsElement)
            && (hasCreditsElement.ValueKind == JsonValueKind.False
                || (hasCreditsElement.ValueKind == JsonValueKind.String
                    && bool.TryParse(hasCreditsElement.GetString(), out var hasCreditsBool)
                    && !hasCreditsBool)))
        {
            return null;
        }

        if (credits.TryGetProperty("unlimited", out var unlimitedElement)
            && unlimitedElement.ValueKind == JsonValueKind.True)
        {
            return "Unlimited";
        }

        if (credits.TryGetProperty("balance", out var balanceElement))
        {
            if (balanceElement.ValueKind == JsonValueKind.String)
            {
                var str = balanceElement.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    var trimmed = str.Trim();
                    if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                        && double.IsFinite(value))
                    {
                        return trimmed;
                    }
                }
            }
            else if (balanceElement.ValueKind == JsonValueKind.Number)
            {
                if (balanceElement.TryGetDouble(out var value) && double.IsFinite(value))
                {
                    return balanceElement.GetRawText();
                }
            }
        }

        return null;
    }

    private static string FormatPlanType(string planType)
    {
        var trimmed = planType.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "free" => "Free",
            "go" => "Go",
            "plus" => "Plus",
            "pro" => "Pro",
            "prolite" => "Pro Lite",
            "promax" => "Pro Max",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "edu" => "Edu",
            "edu_plus" => "Edu Plus",
            "edu_pro" => "Edu Pro",
            "unknown" => "Unknown",
            _ => FormatWords(trimmed)
        };
    }

    private static string FormatReachedType(string reachedType)
    {
        var trimmed = reachedType.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "rate_limit_reached" => "Rate limit reached",
            "workspace_owner_credits_depleted" => "Workspace owner credits depleted",
            "workspace_member_credits_depleted" => "Workspace member credits depleted",
            "workspace_owner_usage_limit_reached" => "Workspace owner usage limit reached",
            "workspace_member_usage_limit_reached" => "Workspace member usage limit reached",
            _ => FormatSentenceCase(trimmed)
        };
    }

    private static string FormatWords(string raw)
    {
        var parts = raw.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return raw;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length > 0)
            {
                parts[i] = char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..].ToLowerInvariant() : string.Empty);
            }
        }

        return string.Join(" ", parts);
    }

    private static string FormatSentenceCase(string raw)
    {
        var normalized = raw.Replace('_', ' ').Trim();
        if (normalized.Length == 0)
        {
            return raw;
        }

        return char.ToUpperInvariant(normalized[0]) + (normalized.Length > 1 ? normalized[1..] : string.Empty);
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
            AppendStreakStat(summary, "currentStreakDays", "Current streak", stats);
            AppendStreakStat(summary, "longestStreakDays", "Longest streak", stats);
            AppendDurationStat(summary, "longestRunningTurnSec", "Longest turn", stats);
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

    private static void AppendStreakStat(JsonElement summary, string propertyName, string label, List<Stat> stats)
    {
        if (!summary.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (!element.TryGetInt64(out var days) || days < 0)
        {
            return;
        }

        var text = days == 1 ? "1 day" : $"{days} days";
        stats.Add(new Stat(label, text, "Reported by Codex"));
    }

    private static void AppendDurationStat(JsonElement summary, string propertyName, string label, List<Stat> stats)
    {
        if (!summary.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (!element.TryGetInt64(out var seconds) || seconds < 0)
        {
            return;
        }

        string formatted;
        if (seconds == 0)
        {
            formatted = "0s";
        }
        else if (seconds < 60)
        {
            formatted = $"{seconds}s";
        }
        else if (seconds > (long)TimeSpan.MaxValue.TotalSeconds)
        {
            formatted = $"{seconds}s";
        }
        else
        {
            try
            {
                var ts = TimeSpan.FromSeconds(seconds);
                var mins = (long)ts.TotalMinutes;
                var remSec = ts.Seconds;
                formatted = remSec == 0 ? $"{mins} min ({seconds}s)" : $"{mins}m {remSec}s ({seconds}s)";
            }
            catch (OverflowException)
            {
                formatted = $"{seconds}s";
            }
        }

        stats.Add(new Stat(label, formatted, "Reported by Codex"));
    }

    private static void AppendResetStat(JsonElement payload, List<Stat> stats)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("rateLimitResetCredits", out var resetObj)
            || resetObj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!resetObj.TryGetProperty("availableCount", out var countEl)
            || countEl.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (!countEl.TryGetInt64(out var availableCount) || availableCount < 0)
        {
            return;
        }

        DateTimeOffset? earliestExpiry = null;
        if (resetObj.TryGetProperty("credits", out var creditsEl)
            && creditsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in creditsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var status = ReadOptionalString(item, "status");
                if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("expiresAt", out var expEl))
                {
                    DateTimeOffset? expiry = null;
                    if (expEl.ValueKind == JsonValueKind.Number && expEl.TryGetInt64(out var seconds)
                        && seconds >= 0 && seconds <= 253402300799)
                    {
                        expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    }
                    if (expiry.HasValue)
                    {
                        if (!earliestExpiry.HasValue || expiry.Value < earliestExpiry.Value)
                        {
                            earliestExpiry = expiry;
                        }
                    }
                }
            }
        }

        var detail = earliestExpiry.HasValue
            ? $"Next known expiry: {earliestExpiry.Value:u}"
            : "";

        stats.Add(new Stat("Rate limit resets", availableCount.ToString(CultureInfo.InvariantCulture), detail));
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
