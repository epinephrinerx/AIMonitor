namespace AIMonitor.Application.Providers;

/// <summary>
/// What a caller is asking a provider client for. This slice only needs "give me the current
/// quota", but the shape carries the history range/metric fields future slices will need so
/// <see cref="IProviderQuotaClient"/> does not have to change signature again when history support
/// lands.
/// </summary>
public sealed record ProviderSnapshotRequest
{
    public const int DefaultHistoryDays = 14;

    /// <summary>Size of the history window a future call should fold, in days. Must be positive;
    /// a client that ignores history (like this slice's Claude live-quota client) is free to ignore
    /// this value entirely.</summary>
    public int HistoryDays { get; }

    /// <summary>Which metric a future history fetch should chart. Empty means "unspecified", not
    /// an error, since this slice never reads it.</summary>
    public string Metric { get; }

    /// <summary>Whether the caller wants history folded into the snapshot at all.</summary>
    public bool IncludeHistory { get; }

    public ProviderSnapshotRequest(
        int historyDays = DefaultHistoryDays,
        string? metric = null,
        bool includeHistory = false)
    {
        if (historyDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(historyDays), historyDays, "History day count must be positive.");
        }

        HistoryDays = historyDays;
        Metric = metric ?? string.Empty;
        IncludeHistory = includeHistory;
    }

    /// <summary>The request a caller sends when it only wants the current quota.</summary>
    public static ProviderSnapshotRequest Default { get; } = new();
}
