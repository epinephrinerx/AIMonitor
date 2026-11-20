using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// Composes the AGY tab's snapshot from the raw AGY quota snapshot plus the cached ccusage report
/// for the <c>antigravity</c> agent (R-018 A2). Pure: no I/O and no clock; the caller supplies
/// <c>today</c>. The quota meters, account, error, detection and setup hint of the raw snapshot
/// are always preserved (D5); only the stats row, the daily history and the value note are added.
/// </summary>
public static class AgyUsageComposer
{
    public const string AgentId = "antigravity";

    /// <summary>
    /// Returns <paramref name="agy"/> unchanged when it already carries history, when there is no
    /// report, or when no <c>antigravity</c> daily row lies in the range (today's look, D4).
    /// Otherwise a copy with every raw field kept, the ccusage daily history and four stats
    /// appended after the AGY stats: token totals over the agent-level daily rows (D4) formatted
    /// like the chart labels, and "Equivalent API value" in <see cref="ChartFormat.Money"/> (D7).
    /// </summary>
    public static ProviderSnapshot Compose(ProviderSnapshot agy, AgentUsageReport? report, DateOnly today, int rangeDays, string metric)
    {
        ArgumentNullException.ThrowIfNull(agy);

        if (agy.History is not null || report is null)
        {
            return agy;
        }

        if (rangeDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeDays), rangeDays, "Range must be greater than zero.");
        }

        var start = today.AddDays(-(rangeDays - 1));
        long totalTokens = 0, outputTokens = 0, cacheReads = 0;
        var rowsInRange = new List<AgentUsage>();
        foreach (var daily in report.Daily)
        {
            if (daily.Date < start || daily.Date > today)
            {
                continue;
            }

            foreach (var agent in daily.Agents)
            {
                if (!string.Equals(agent.Agent, AgentId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // D4: an agent-level row counts in the tiles even without model rows.
                rowsInRange.Add(agent);
                totalTokens += agent.Tokens.Total;
                outputTokens += agent.Tokens.Output;
                cacheReads += agent.Tokens.CacheRead;
            }
        }

        if (rowsInRange.Count == 0)
        {
            return agy;
        }

        var stats = new List<Stat>(agy.Stats.Count + 4);
        stats.AddRange(agy.Stats);
        stats.Add(new Stat("Total tokens", ChartFormat.Compact(totalTokens), "ccusage · antigravity"));
        stats.Add(new Stat("Output tokens", ChartFormat.Compact(outputTokens), "ccusage · antigravity"));
        stats.Add(new Stat("Cache reads", ChartFormat.Compact(cacheReads), "ccusage · antigravity"));
        // D7: $0.00 with no models, "Unknown price" when all are unpriced, "$X.XX + unpriced" when
        // priced and unpriced models mix, "$X.XX" when all priced.
        stats.Add(new Stat("Equivalent API value", AgentUsagePresenter.CostText(rowsInRange), "ccusage · equivalent API price"));

        // D9: ccusage data was composed, so the note tells where each half comes from.
        return new ProviderSnapshot(
            providerId: agy.ProviderId,
            configured: agy.Configured,
            meters: agy.Meters,
            stats: stats,
            history: AgentHistoryBuilder.Build(report, AgentId, today, rangeDays, metric),
            account: agy.Account,
            error: agy.Error,
            historyError: agy.HistoryError,
            unauthorized: agy.Unauthorized,
            setupHint: agy.SetupHint,
            valueNote: "Quota from AGY CLI · tokens and cost from ccusage",
            fetchedAt: agy.FetchedAt,
            detection: agy.Detection);
    }
}
