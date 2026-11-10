using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Controls;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// Builds the Overview tab's snapshot from the cached ccusage report and the configured budgets.
/// Pure: no I/O and no clock; the caller supplies <c>today</c>, <c>now</c> and the local zone.
/// One gauge per ccusage agent reported for the current local month (R-016 H1.1).
/// </summary>
public static class OverviewSnapshotBuilder
{
    public const string ProviderId = "overview";

    /// <summary>
    /// Returns <c>null</c> while ccusage has not reported yet (E23), an error-only snapshot when the
    /// last read failed with no cached report (E10), or the multi-agent month-to-date budget snapshot.
    /// </summary>
    public static ProviderSnapshot? Build(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        IReadOnlyDictionary<string, decimal> budgets,
        DateOnly today,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(zone);

        if (lastGood is null && lastError is null)
        {
            // E23: keep the tab's initial "Checking ccusage..." state; no snapshot is pushed.
            return null;
        }

        if (lastGood is null)
        {
            // E10: configured:true keeps the setup card away so only the error banner is drawn.
            return new ProviderSnapshot(
                providerId: ProviderId,
                configured: true,
                error: AgentUsagePresenter.ErrorText(lastError!.Kind, lastError.Message),
                fetchedAt: now);
        }

        var resetsAt = MonthBoundary.FirstOfNextMonth(today, zone);

        // I1: the Monthly section is authoritative (same run as the OpenCode gauge).
        var month = lastGood.Monthly.FirstOrDefault(m => m.Year == today.Year && m.Month == today.Month);

        var entries = new List<(Meter Meter, decimal Cost)>();
        if (month is not null)
        {
            // I2: duplicate agent names within the month (any case) produce one meter; first row wins.
            // AgentUsage already lowercases Agent, so a distinct-by-name keeps the first occurrence.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var agentUsage in month.Agents)
            {
                if (!seen.Add(agentUsage.Agent))
                {
                    continue;
                }

                var reading = AgentBudget.Compute(lastGood, agentUsage.Agent, budgets, today);
                if (reading is null)
                {
                    continue;
                }

                entries.Add((BuildMeter(reading, resetsAt), reading.MonthCostUsd));
            }
        }

        // O1: MonthCostUsd descending; ties by agent name (ordinal ascending).
        entries.Sort((a, b) =>
        {
            var cmp = b.Cost.CompareTo(a.Cost);
            return cmp != 0 ? cmp : string.Compare(a.Meter.Title, b.Meter.Title, StringComparison.Ordinal);
        });

        var meters = entries.Select(e => e.Meter).ToList();

        return new ProviderSnapshot(
            providerId: ProviderId,
            configured: true,
            meters: meters,
            history: null,
            fetchedAt: lastGood.RetrievedAt);
    }

    private static Meter BuildMeter(AgentBudgetReading reading, DateTimeOffset resetsAt)
    {
        var spent = ChartFormat.Money((double)reading.MonthCostUsd);
        if (reading.HasUnpricedModels)
        {
            spent += " + unpriced";
        }

        var subtitle = reading.BudgetUsd is { } budget
            ? $"{spent} of {ChartFormat.Money((double)budget)} budget"
            : $"{spent} · No budget set — add one in Settings";

        return new Meter(
            kind: "month_budget",
            key: "agent:" + reading.Agent,
            title: reading.Agent,
            subtitle: subtitle,
            percent: reading.Percent,
            resetsAt: resetsAt,
            detail: spent);
    }
}
