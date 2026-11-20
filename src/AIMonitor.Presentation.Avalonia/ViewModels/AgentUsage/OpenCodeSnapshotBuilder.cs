using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// Builds the OpenCode tab's snapshot from the cached ccusage report and the configured budgets.
/// Pure: no I/O and no clock; the caller supplies <c>today</c>, <c>now</c> and the local zone.
/// </summary>
public static class OpenCodeSnapshotBuilder
{
    public const string ProviderId = "opencode";

    private const string Agent = "opencode";

    /// <summary>
    /// Returns <c>null</c> while ccusage has not reported yet (E23), an error-only snapshot when the
    /// last read failed with no cached report (E10), or the month-to-date budget snapshot.
    /// </summary>
    public static ProviderSnapshot? Build(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        IReadOnlyDictionary<string, decimal> budgets,
        DateOnly today,
        DateTimeOffset now,
        TimeZoneInfo zone,
        int rangeDays,
        string metric)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(zone);

        if (lastGood is null && lastError is null)
        {
            // E23: keep the tab's initial "Checking..." state; no snapshot is pushed.
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

        var reading = AgentBudget.Compute(lastGood, Agent, budgets, today)!;
        // E26: the shared gauge card shows no Detail, so the money also goes into the subtitle.
        var spent = ChartFormat.Money((double)reading.MonthCostUsd);
        if (reading.HasUnpricedModels)
        {
            spent += " + unpriced";
        }

        var subtitle = reading.BudgetUsd is { } budget
            ? $"{spent} of {ChartFormat.Money((double)budget)} budget"
            : $"{spent} · No budget set — add one in Settings";

        var meter = new Meter(
            kind: "month_budget",
            key: "month_budget",
            title: "Month to date",
            subtitle: subtitle,
            percent: reading.Percent,
            resetsAt: MonthBoundary.FirstOfNextMonth(today, zone),
            detail: spent);

        return new ProviderSnapshot(
            providerId: ProviderId,
            configured: true,
            meters: [meter],
            history: OpenCodeHistoryBuilder.Build(lastGood, today, rangeDays, metric),
            fetchedAt: lastGood.RetrievedAt);
    }
}
