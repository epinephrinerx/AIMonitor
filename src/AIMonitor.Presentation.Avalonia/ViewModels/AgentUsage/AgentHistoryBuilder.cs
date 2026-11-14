using AIMonitor.Domain;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// Builds a provider tab's daily history from the cached ccusage report for one agent (R-018 A1).
/// Pure: no I/O and no clock; the caller supplies <c>today</c> and the agent id. Only that agent's
/// models contribute to the day buckets, the series, the by-model breakdown and the optional cost
/// line; other agents are ignored.
/// </summary>
public static class AgentHistoryBuilder
{
    /// <summary>
    /// Returns <c>null</c> when there is no report; otherwise a history covering the
    /// <paramref name="rangeDays"/> local days ending at <paramref name="today"/> (oldest first),
    /// including empty days. Unknown metric strings fall back to total tokens.
    /// </summary>
    public static UsageHistory? Build(AgentUsageReport? report, string agentId, DateOnly today, int rangeDays, string metric)
    {
        if (report is null)
        {
            return null;
        }

        if (rangeDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeDays), rangeDays, "Range must be greater than zero.");
        }

        var normalisedMetric = NormaliseMetric(metric);
        var start = today.AddDays(-(rangeDays - 1));
        var days = new List<DateOnly>(rangeDays);
        for (var offset = 0; offset < rangeDays; offset++)
        {
            days.Add(start.AddDays(offset));
        }

        var values = new Dictionary<DateOnly, Dictionary<string, double>>(rangeDays);
        var costs = new Dictionary<DateOnly, DayCost>(rangeDays);
        foreach (var day in days)
        {
            values[day] = new Dictionary<string, double>(StringComparer.Ordinal);
            costs[day] = new DayCost();
        }

        foreach (var daily in report.Daily)
        {
            if (daily.Date < start || daily.Date > today)
            {
                continue;
            }

            var agent = FindAgent(daily.Agents, agentId);
            if (agent is null)
            {
                continue;
            }

            var dayValues = values[daily.Date];
            var dayCost = costs[daily.Date];
            foreach (var model in agent.Models)
            {
                var value = ValueFor(model, normalisedMetric);
                if (value > 0)
                {
                    dayValues[model.Model] = dayValues.GetValueOrDefault(model.Model) + value;
                }

                if (model.CostUsd is { } priced)
                {
                    dayCost.PricedUsd += (double)priced;
                }
                else
                {
                    dayCost.HasUnpricedModels = true;
                }
            }
        }

        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var day in days)
        {
            foreach (var (model, value) in values[day])
            {
                totals[model] = totals.GetValueOrDefault(model) + value;
            }
        }

        var models = totals
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .ToList();

        var buckets = days
            .Select(day => new UsageHistoryBucket(day, values[day]))
            .ToList();
        var byModel = models
            .Select(model => new UsageHistoryBreakdown(model, totals[model]))
            .ToList();
        var dailyCosts = days
            .Select(day => new DailyCost(day, costs[day].PricedUsd, costs[day].HasUnpricedModels))
            .ToList();

        return new UsageHistory(
            buckets: buckets,
            series: models,
            byModel: byModel,
            byProject: [],
            days: rangeDays,
            metric: normalisedMetric,
            dailyCosts: dailyCosts);
    }

    private static AgentUsage? FindAgent(IReadOnlyList<AgentUsage> agents, string agentId)
    {
        foreach (var agent in agents)
        {
            if (string.Equals(agent.Agent, agentId, StringComparison.OrdinalIgnoreCase))
            {
                return agent;
            }
        }

        return null;
    }

    private static double ValueFor(ModelUsage model, string metric) => metric switch
    {
        UsageMetric.OutputTokens => model.Tokens.Output,
        UsageMetric.EquivalentValue => model.CostUsd is { } cost ? (double)cost : 0d,
        _ => model.Tokens.Total,
    };

    private static string NormaliseMetric(string metric) => metric switch
    {
        UsageMetric.OutputTokens => UsageMetric.OutputTokens,
        UsageMetric.EquivalentValue => UsageMetric.EquivalentValue,
        _ => UsageMetric.TotalTokens,
    };

    private sealed class DayCost
    {
        public double PricedUsd { get; set; }

        public bool HasUnpricedModels { get; set; }
    }
}
