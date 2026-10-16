using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Agy;

/// <summary>
/// Pure parser and mapper for AGY CLI usage and quota command output envelopes.
/// </summary>
public static class AgyUsageMapper
{
    private const int MaxJsonLength = 512 * 1024;
    private const int MaxGroups = 100;
    private const int MaxBucketsPerGroup = 100;
    private const int MaxTextLength = 256;

    // Strict ISO 8601 requiring both date and time, with mandatory 'Z' or numeric offset (+HH:mm / -HH:mm / +HHmm / -HHmm).
    // Rejects date-only strings (e.g. 2026-10-15) and time strings lacking offset (e.g. 2026-10-15T12:00:00).
    private static readonly Regex Iso8601WithOffsetRegex = new(
        @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}(:\d{2}(\.\d+)?)?([Zz]|[+-]\d{2}:?\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlyList<Meter> MapUsage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new AgyUsageException("Empty response from AGY CLI.", AgyErrorKind.InvalidResponse);
        }

        if (json.Length > MaxJsonLength)
        {
            throw new AgyUsageException("Response exceeds maximum allowed size.", AgyErrorKind.InvalidResponse);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AgyUsageException("Invalid response envelope from AGY CLI.", AgyErrorKind.InvalidResponse);
            }

            if (!root.TryGetProperty("status", out var statusProp) ||
                statusProp.ValueKind != JsonValueKind.String ||
                !string.Equals(statusProp.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgyUsageException("AGY CLI command returned non-success status.", AgyErrorKind.InvalidResponse);
            }

            if (!root.TryGetProperty("command", out var commandProp) || commandProp.ValueKind != JsonValueKind.Object)
            {
                throw new AgyUsageException("Invalid response envelope from AGY CLI.", AgyErrorKind.InvalidResponse);
            }

            if (!commandProp.TryGetProperty("name", out var nameProp) ||
                nameProp.ValueKind != JsonValueKind.String)
            {
                throw new AgyUsageException("Invalid command name in response envelope.", AgyErrorKind.InvalidResponse);
            }

            var commandName = nameProp.GetString();
            if (!string.Equals(commandName, "usage", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(commandName, "quota", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgyUsageException("Unrecognized command name in response envelope.", AgyErrorKind.InvalidResponse);
            }

            if (!commandProp.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Object)
            {
                throw new AgyUsageException("Missing command data in response envelope.", AgyErrorKind.InvalidResponse);
            }

            if (!dataProp.TryGetProperty("groups", out var groupsProp) || groupsProp.ValueKind != JsonValueKind.Array)
            {
                throw new AgyUsageException("Missing groups array in response data.", AgyErrorKind.InvalidResponse);
            }

            int groupCount = groupsProp.GetArrayLength();
            if (groupCount > MaxGroups)
            {
                throw new AgyUsageException("Response contains too many groups.", AgyErrorKind.InvalidResponse);
            }

            var meters = new List<Meter>();
            var seenGlobalKeys = new HashSet<string>(StringComparer.Ordinal);

            int groupIndex = 0;
            foreach (var groupElem in groupsProp.EnumerateArray())
            {
                if (groupElem.ValueKind != JsonValueKind.Object)
                {
                    throw new AgyUsageException("Invalid group object in response.", AgyErrorKind.InvalidResponse);
                }

                if (!groupElem.TryGetProperty("name", out var groupNameProp) ||
                    groupNameProp.ValueKind != JsonValueKind.String)
                {
                    throw new AgyUsageException("Group name is missing.", AgyErrorKind.InvalidResponse);
                }

                var groupName = groupNameProp.GetString();
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    throw new AgyUsageException("Group name cannot be blank.", AgyErrorKind.InvalidResponse);
                }

                if (groupName.Length > MaxTextLength)
                {
                    throw new AgyUsageException("Group name exceeds maximum length.", AgyErrorKind.InvalidResponse);
                }

                if (!groupElem.TryGetProperty("buckets", out var bucketsProp) ||
                    bucketsProp.ValueKind != JsonValueKind.Array)
                {
                    throw new AgyUsageException("Group buckets must be an array.", AgyErrorKind.InvalidResponse);
                }

                if (bucketsProp.GetArrayLength() > MaxBucketsPerGroup)
                {
                    throw new AgyUsageException("Group contains too many buckets.", AgyErrorKind.InvalidResponse);
                }

                var seenBucketIdsInGroup = new HashSet<string>(StringComparer.Ordinal);
                var validGroupBuckets = new List<IntermediateBucket>();

                foreach (var bucketElem in bucketsProp.EnumerateArray())
                {
                    if (bucketElem.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!bucketElem.TryGetProperty("id", out var idProp) ||
                        idProp.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var bucketId = idProp.GetString();
                    if (string.IsNullOrWhiteSpace(bucketId))
                    {
                        continue;
                    }

                    if (bucketId.Length > MaxTextLength)
                    {
                        throw new AgyUsageException("Bucket id exceeds maximum length.", AgyErrorKind.InvalidResponse);
                    }

                    if (!seenBucketIdsInGroup.Add(bucketId))
                    {
                        throw new AgyUsageException("Duplicate bucket ID in group.", AgyErrorKind.InvalidResponse);
                    }

                    if (!bucketElem.TryGetProperty("window", out var windowProp) ||
                        windowProp.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var window = windowProp.GetString();
                    bool is5h = string.Equals(window, "5h", StringComparison.OrdinalIgnoreCase);
                    bool isWeekly = string.Equals(window, "weekly", StringComparison.OrdinalIgnoreCase);

                    if (!is5h && !isWeekly)
                    {
                        continue;
                    }

                    if (!bucketElem.TryGetProperty("remaining_fraction", out var fractionProp) ||
                        fractionProp.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    if (!fractionProp.TryGetDouble(out double fraction) ||
                        double.IsNaN(fraction) ||
                        double.IsInfinity(fraction) ||
                        fraction < 0.0 ||
                        fraction > 1.0)
                    {
                        continue;
                    }

                    DateTimeOffset? resetsAt = null;
                    if (bucketElem.TryGetProperty("reset_time", out var resetProp) &&
                        resetProp.ValueKind == JsonValueKind.String)
                    {
                        resetsAt = ParseResetTime(resetProp.GetString());
                    }

                    double percent = Math.Round((1.0 - fraction) * 100.0, 2);

                    validGroupBuckets.Add(new IntermediateBucket(
                        groupIndex,
                        groupName,
                        bucketId,
                        is5h,
                        percent,
                        resetsAt));
                }

                // Stable sort within group: 5h windows before weekly windows.
                foreach (var bucket in validGroupBuckets.OrderBy(b => b.Is5h ? 0 : 1))
                {
                    string kind = bucket.Is5h ? "session" : "weekly";
                    var displayGroup = bucket.GroupName switch
                    {
                        "Gemini Models" => "Gemini",
                        "Claude and GPT models" => "Claude / GPT",
                        _ => bucket.GroupName
                    };
                    // Model-specific Gemini pools (e.g. "Gemini 4 Argon") sit with the shared Gemini pool.
                    var section = displayGroup.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase)
                        ? "Gemini"
                        : displayGroup;
                    string title = bucket.Is5h
                        ? $"{displayGroup} · Session"
                        : $"{displayGroup} · Weekly";
                    string subtitle = bucket.Is5h ? "5-hour window" : "Weekly window";
                    string key = $"agy_{bucket.GroupIndex}_{bucket.BucketId}";

                    if (!seenGlobalKeys.Add(key))
                    {
                        throw new AgyUsageException("Duplicate meter key generated.", AgyErrorKind.InvalidResponse);
                    }

                    meters.Add(new Meter(
                        kind: kind,
                        key: key,
                        title: title,
                        subtitle: subtitle,
                        percent: bucket.Percent,
                        resetsAt: bucket.ResetsAt,
                        section: section));
                }

                groupIndex++;
            }

            if (meters.Count == 0)
            {
                throw new AgyUsageException("No valid quota windows returned from AGY CLI.", AgyErrorKind.InvalidResponse);
            }

            return meters;
        }
        catch (AgyUsageException)
        {
            throw;
        }
        catch (Exception)
        {
            // Raw response text and inner exception details are never exposed.
            throw new AgyUsageException("Invalid response from AGY CLI.", AgyErrorKind.InvalidResponse);
        }
    }

    private static DateTimeOffset? ParseResetTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!Iso8601WithOffsetRegex.IsMatch(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            return dto;
        }

        return null;
    }

    private sealed record IntermediateBucket(
        int GroupIndex,
        string GroupName,
        string BucketId,
        bool Is5h,
        double Percent,
        DateTimeOffset? ResetsAt);
}
