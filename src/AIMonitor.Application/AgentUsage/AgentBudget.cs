using System.Globalization;

namespace AIMonitor.Application.AgentUsage;

/// <summary>
/// A single monthly-budget reading for one ccusage agent.
/// </summary>
public sealed record AgentBudgetReading(
    string Agent,
    decimal MonthCostUsd,
    bool HasUnpricedModels,
    decimal? BudgetUsd,
    double? Percent,
    bool HasUsageThisMonth);

/// <summary>
/// Pure calculator that turns a ccusage report and a set of per-agent budgets into a
/// <see cref="AgentBudgetReading"/>.
/// </summary>
public static class AgentBudget
{
    /// <summary>
    /// Computes the monthly budget reading for <paramref name="agent"/>.
    /// Returns <c>null</c> when the report is absent; never throws for a valid report.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="agent"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="budgets"/> is <c>null</c>.</exception>
    public static AgentBudgetReading? Compute(
        Domain.AgentUsageReport? report,
        string agent,
        IReadOnlyDictionary<string, decimal> budgets,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (string.IsNullOrWhiteSpace(agent))
        {
            throw new ArgumentException("Agent must not be blank.", nameof(agent));
        }

        if (report is null)
        {
            return null;
        }

        var normalisedAgent = agent.Trim().ToLowerInvariant();

        var month = report.Monthly.FirstOrDefault(m => m.Year == today.Year && m.Month == today.Month);

        var hasUsageThisMonth = false;
        var hasUnpricedModels = false;
        var monthCostUsd = 0m;

        if (month is not null)
        {
            var usage = month.Agents.FirstOrDefault(
                a => string.Equals(a.Agent, normalisedAgent, StringComparison.OrdinalIgnoreCase));

            if (usage is not null)
            {
                hasUsageThisMonth = true;
                monthCostUsd = usage.PricedCostUsd;
                hasUnpricedModels = usage.HasUnpricedModels;
            }
        }

        decimal? budgetUsd = null;
        if (budgets.TryGetValue(normalisedAgent, out var budgetValue) && budgetValue > 0m)
        {
            budgetUsd = budgetValue;
        }
        else
        {
            // E21: find the budget case-insensitively even when the caller's dictionary uses the default comparer.
            foreach (var pair in budgets)
            {
                if (string.Equals(pair.Key.Trim(), normalisedAgent, StringComparison.OrdinalIgnoreCase)
                    && pair.Value > 0m)
                {
                    budgetUsd = pair.Value;
                    break;
                }
            }
        }

        double? percent = budgetUsd.HasValue
            ? (double)(monthCostUsd / budgetUsd.Value * 100m)
            : null;

        return new AgentBudgetReading(
            normalisedAgent,
            monthCostUsd,
            hasUnpricedModels,
            budgetUsd,
            percent,
            hasUsageThisMonth);
    }
}
