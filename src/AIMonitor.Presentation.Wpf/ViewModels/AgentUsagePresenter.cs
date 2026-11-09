using System.Globalization;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Controls;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// Pure presentation mapping from <see cref="AgentUsageReport"> domain records to view-model-ready
/// strings and records. Keeps WPF types out so the rules are unit-testable.
/// </summary>
public static class AgentUsagePresenter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string AgentDisplayName(string agent) => agent.ToLowerInvariant() switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "antigravity" => "Antigravity",
        "opencode" => "OpenCode",
        "copilot" => "Copilot",
        "zcode" => "ZCode",
        _ => agent,
    };

    public static string TokensText(long tokens) => ChartFormat.Compact(tokens);

    public static string CostText(AgentUsage usage)
    {
        var hasPriced = false;
        var hasUnpriced = false;
        decimal pricedCost = 0;
        foreach (var model in usage.Models)
        {
            if (model.CostUsd.HasValue)
            {
                hasPriced = true;
                pricedCost += model.CostUsd.Value;
            }
            else
            {
                hasUnpriced = true;
            }
        }

        return FormatCost(pricedCost, hasPriced, hasUnpriced);
    }

    public static string CostText(IReadOnlyList<AgentUsage> agents)
    {
        var hasPriced = false;
        var hasUnpriced = false;
        decimal pricedCost = 0;
        foreach (var agent in agents)
        {
            foreach (var model in agent.Models)
            {
                if (model.CostUsd.HasValue)
                {
                    hasPriced = true;
                    pricedCost += model.CostUsd.Value;
                }
                else
                {
                    hasUnpriced = true;
                }
            }
        }

        return FormatCost(pricedCost, hasPriced, hasUnpriced);
    }

    private static string FormatCost(decimal pricedCost, bool hasPriced, bool hasUnpriced)
    {
        if (!hasPriced && !hasUnpriced)
        {
            return "$0.00";
        }

        if (!hasPriced)
        {
            return "Unknown price";
        }

        var money = ChartFormat.Money((double)pricedCost);
        return hasUnpriced ? $"{money} + unpriced" : money;
    }

    public static AgentUsagePeriod BuildDay(DailyAgentUsage day)
    {
        var sortedAgents = day.Agents
            .OrderByDescending(a => a.PricedCostUsd)
            .ThenByDescending(a => a.Tokens.Total)
            .ThenBy(a => a.Agent, StringComparer.Ordinal)
            .ToList();

        var lines = sortedAgents
            .Select(a => new AgentUsageLine(
                a.Agent,
                AgentDisplayName(a.Agent),
                a.Tokens.Total,
                TokensText(a.Tokens.Total),
                CostText(a)))
            .ToList();

        var totalTokens = sortedAgents.Sum(a => a.Tokens.Total);
        return new AgentUsagePeriod(
            day.Date.ToString("yyyy-MM-dd", Inv),
            totalTokens,
            TokensText(totalTokens),
            CostText(sortedAgents),
            lines);
    }

    public static AgentUsagePeriod BuildMonth(MonthlyAgentUsage month)
    {
        var sortedAgents = month.Agents
            .OrderByDescending(a => a.PricedCostUsd)
            .ThenByDescending(a => a.Tokens.Total)
            .ThenBy(a => a.Agent, StringComparer.Ordinal)
            .ToList();

        var lines = sortedAgents
            .Select(a => new AgentUsageLine(
                a.Agent,
                AgentDisplayName(a.Agent),
                a.Tokens.Total,
                TokensText(a.Tokens.Total),
                CostText(a)))
            .ToList();

        var totalTokens = sortedAgents.Sum(a => a.Tokens.Total);
        var title = new DateOnly(month.Year, month.Month, 1).ToString("MMMM yyyy", Inv);
        return new AgentUsagePeriod(
            title,
            totalTokens,
            TokensText(totalTokens),
            CostText(sortedAgents),
            lines);
    }

    public static AgentUsageSessionLine BuildSession(SessionAgentUsage session)
    {
        var displayName = AgentDisplayName(session.Usage.Agent);
        var shortId = session.SessionId.Length <= 8
            ? session.SessionId
            : session.SessionId[..8];
        var localTime = session.LastActivity.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv);
        return new AgentUsageSessionLine(
            displayName,
            shortId,
            localTime,
            TokensText(session.Usage.Tokens.Total),
            CostText(session.Usage));
    }

    public static string PricingText(AgentUsagePricing pricing) => pricing switch
    {
        AgentUsagePricing.OnlineRequested => "Prices: online",
        AgentUsagePricing.Offline => "Prices: offline (built-in)",
        _ => "Prices: unknown",
    };

    public static string? PricingToolTip(AgentUsagePricing pricing) => pricing switch
    {
        AgentUsagePricing.OnlineRequested => "ccusage requested online prices; it uses its built-in prices if the fetch fails.",
        _ => null,
    };

    public static string ErrorText(AgentUsageErrorKind kind, string message) => kind switch
    {
        AgentUsageErrorKind.NotInstalled => message,
        _ => message,
    };

    public static AgentUsageSummary? SummaryFor(AgentUsageReport? report, string agent, DateOnly today)
    {
        if (report is null)
        {
            return null;
        }

        AgentUsageLine? todayLine = null;
        AgentUsageLine? monthLine = null;

        var daily = report.Daily.FirstOrDefault(d => d.Date == today);
        if (daily is not null)
        {
            var usage = daily.Agents.FirstOrDefault(a => a.Agent == agent);
            if (usage is not null)
            {
                todayLine = new AgentUsageLine(
                    usage.Agent,
                    AgentDisplayName(usage.Agent),
                    usage.Tokens.Total,
                    TokensText(usage.Tokens.Total),
                    CostText(usage));
            }
        }

        var monthly = report.Monthly.FirstOrDefault(m => m.Year == today.Year && m.Month == today.Month);
        if (monthly is not null)
        {
            var usage = monthly.Agents.FirstOrDefault(a => a.Agent == agent);
            if (usage is not null)
            {
                monthLine = new AgentUsageLine(
                    usage.Agent,
                    AgentDisplayName(usage.Agent),
                    usage.Tokens.Total,
                    TokensText(usage.Tokens.Total),
                    CostText(usage));
            }
        }

        return todayLine is null && monthLine is null ? null : new AgentUsageSummary(todayLine, monthLine);
    }
}

public sealed record AgentUsagePeriod(
    string Title,
    long Tokens,
    string TokensText,
    string CostText,
    IReadOnlyList<AgentUsageLine> Agents);

public sealed record AgentUsageLine(
    string Agent,
    string DisplayName,
    long Tokens,
    string TokensText,
    string CostText);

public sealed record AgentUsageSessionLine(
    string DisplayName,
    string ShortId,
    string LastActivityText,
    string TokensText,
    string CostText);

public sealed record AgentUsageSummary(AgentUsageLine? Today, AgentUsageLine? ThisMonth);
