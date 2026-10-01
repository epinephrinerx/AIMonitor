namespace AIMonitor.Domain;

/// <summary>
/// Orders meters by what each window *is*, never by how full it is, so the row a user reads by
/// position does not reshuffle itself as percentages change.
/// </summary>
public static class MeterReadingOrder
{
    /// <summary>
    /// Session first, the whole-account weekly window second, per-model weekly windows next in
    /// case-insensitive subtitle order, and any unrecognised kind last. Nothing is dropped, and the
    /// result does not depend on input order or on <see cref="Meter.Percent"/>.
    /// </summary>
    public static IReadOnlyList<Meter> Sort(IEnumerable<Meter> meters)
    {
        ArgumentNullException.ThrowIfNull(meters);

        var materialized = meters.ToArray();
        RequireUniqueMeterKeys(materialized);

        return materialized
            .OrderBy(RankOf)
            .ThenBy(meter => meter.Subtitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(meter => meter.Kind, StringComparer.Ordinal)
            .ThenBy(meter => meter.Key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Two meters sharing one key would make reading order ambiguous. Compared with ordinal
    /// identity, matching the exact comparer used above to break ties.
    /// </summary>
    private static void RequireUniqueMeterKeys(IReadOnlyList<Meter> meters)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meter in meters)
        {
            if (!seen.Add(meter.Key))
            {
                throw new ArgumentException($"Duplicate meter key '{meter.Key}'.", nameof(meters));
            }
        }
    }

    private static int RankOf(Meter meter)
    {
        if (meter.Kind == "session" || meter.Group == "session")
        {
            return 0;
        }

        if (meter.Kind == "weekly_all")
        {
            return 1;
        }

        return meter.Kind.StartsWith("weekly", StringComparison.Ordinal) ? 2 : 3;
    }
}
