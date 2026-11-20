using AIMonitor.Domain;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// Builds the OpenCode tab's daily history from the cached ccusage report (R-018 A1). Pure: no I/O
/// and no clock; the caller supplies <c>today</c>. Delegates to <see cref="AgentHistoryBuilder"/>
/// with the <c>opencode</c> agent, so only that agent's models contribute to the day buckets, the
/// series, the by-model breakdown and the optional cost line.
/// </summary>
public static class OpenCodeHistoryBuilder
{
    public const string AgentId = "opencode";

    /// <summary>
    /// Returns <c>null</c> when there is no report; otherwise a history covering the
    /// <paramref name="rangeDays"/> local days ending at <paramref name="today"/> (oldest first),
    /// including empty days. Unknown metric strings fall back to total tokens.
    /// </summary>
    public static UsageHistory? Build(AgentUsageReport? report, DateOnly today, int rangeDays, string metric) =>
        AgentHistoryBuilder.Build(report, AgentId, today, rangeDays, metric);
}
