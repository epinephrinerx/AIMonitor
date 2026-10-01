using System.Collections.ObjectModel;

namespace AIMonitor.Domain;

/// <summary>Everything one provider tab needs to render itself for a single refresh.</summary>
public sealed record ProviderSnapshot
{
    public string ProviderId { get; }

    public bool Configured { get; }

    public IReadOnlyList<Meter> Meters { get; }

    public IReadOnlyList<Stat> Stats { get; }

    public UsageHistory? History { get; }

    public string Account { get; }

    /// <summary>A refresh that produced nothing usable. Non-null means the service failed.</summary>
    public string? Error { get; }

    /// <summary>
    /// The history could not be read, but everything else in this snapshot is good. Kept separate
    /// from <see cref="Error"/> so a history failure never hides live quota data.
    /// </summary>
    public string? HistoryError { get; }

    public bool Unauthorized { get; }

    public string SetupHint { get; }

    public string ValueNote { get; }

    public DateTimeOffset? FetchedAt { get; }

    public DetectionInfo? Detection { get; }

    public bool Ok => Configured && Error is null;

    public ProviderSnapshot(
        string providerId,
        bool configured = false,
        IReadOnlyList<Meter>? meters = null,
        IReadOnlyList<Stat>? stats = null,
        UsageHistory? history = null,
        string account = "",
        string? error = null,
        string? historyError = null,
        bool unauthorized = false,
        string setupHint = "",
        string valueNote = "",
        DateTimeOffset? fetchedAt = null,
        DetectionInfo? detection = null)
    {
        ProviderId = DomainGuard.RequireNonBlank(providerId, nameof(providerId));
        Configured = configured;
        Meters = DomainGuard.ToReadOnlyCopy(meters);
        RequireUniqueMeterKeys(Meters);
        Stats = DomainGuard.ToReadOnlyCopy(stats);
        History = history;
        Account = account ?? string.Empty;
        Error = error;
        HistoryError = historyError;
        Unauthorized = unauthorized;
        SetupHint = setupHint ?? string.Empty;
        ValueNote = valueNote ?? string.Empty;
        FetchedAt = fetchedAt;
        Detection = detection;
    }

    /// <summary>
    /// Two meters sharing one key would make reading order ambiguous and let a consumer overwrite
    /// one window's state with another's. Compared with ordinal identity, matching the exact
    /// comparer <see cref="MeterReadingOrder"/> uses to break ties.
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
}
