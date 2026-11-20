using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests;

/// <summary>Shared builders, so each test says only what it is actually about.</summary>
internal static class TestData
{
    public static Meter Meter(
        string key = "session",
        string kind = "session",
        string title = "Session",
        string subtitle = "5-hour window",
        double? percent = 12,
        Severity severity = Severity.Normal,
        DateTimeOffset? resetsAt = null,
        string section = "") =>
        new(kind: kind,
            key: key,
            title: title,
            subtitle: subtitle,
            percent: percent,
            serverSeverity: severity,
            resetsAt: resetsAt,
            section: section);

    /// <summary>A history with one bucket per day, newest last, split across two models.</summary>
    public static UsageHistory History(
        int buckets = 14,
        int days = 14,
        string metric = "Total tokens",
        string projectLabel = "By project")
    {
        var today = DateOnly.FromDateTime(new DateTime(2026, 10, 31));
        var dayBuckets = Enumerable.Range(0, buckets)
            .Select(offset => new UsageHistoryBucket(
                today.AddDays(-(buckets - 1 - offset)),
                new Dictionary<string, double> { ["sonnet"] = 1000 + offset, ["opus"] = 500 }))
            .ToList();

        return new UsageHistory(
            buckets: dayBuckets,
            series: ["sonnet", "opus"],
            byModel: [new UsageHistoryBreakdown("sonnet", 14000), new UsageHistoryBreakdown("opus", 7000)],
            byProject: [new UsageHistoryBreakdown("AIMonitor", 21000)],
            days: days,
            metric: metric,
            projectLabel: projectLabel);
    }

    public static ProviderSnapshot Snapshot(
        string providerId = "claude",
        bool configured = true,
        IReadOnlyList<Meter>? meters = null,
        string? error = null,
        string account = "",
        string? historyError = null,
        IReadOnlyList<Stat>? stats = null,
        UsageHistory? history = null) =>
        new(providerId,
            configured: configured,
            meters: meters ?? [Meter()],
            stats: stats,
            history: history,
            error: error,
            historyError: historyError,
            account: account);

    public static ProviderViewModel Provider(
        string providerId = "claude",
        string title = "Claude",
        ProviderSnapshot? snapshot = null)
    {
        var provider = new ProviderViewModel(providerId, title);
        provider.Apply(snapshot ?? Snapshot(providerId));
        return provider;
    }
}
