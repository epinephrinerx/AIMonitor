using System.Globalization;

namespace AIMonitor.Domain;

/// <summary>
/// Indicates whether ccusage was asked for online prices or fell back to its built-in price table.
/// </summary>
public enum AgentUsagePricing
{
    OnlineRequested,
    Offline,
}

/// <summary>
/// Token counts for an agent or model. The values are pure and validated at construction.
/// </summary>
public sealed record TokenCounts
{
    public long Input { get; }
    public long Output { get; }
    public long CacheCreation { get; }
    public long CacheRead { get; }
    public long Total { get; }

    /// <summary>
    /// Tokens that ccusage counted in <see cref="Total"/> but did not bucket into the four
    /// detailed counters.
    /// </summary>
    public long Other => Total - (Input + Output + CacheCreation + CacheRead);

    public TokenCounts(long input, long output, long cacheCreation, long cacheRead, long total)
    {
        if (input < 0) throw new ArgumentOutOfRangeException(nameof(input));
        if (output < 0) throw new ArgumentOutOfRangeException(nameof(output));
        if (cacheCreation < 0) throw new ArgumentOutOfRangeException(nameof(cacheCreation));
        if (cacheRead < 0) throw new ArgumentOutOfRangeException(nameof(cacheRead));
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        var bucketSum = input + output + cacheCreation + cacheRead;
        if (total < bucketSum)
            throw new ArgumentOutOfRangeException(nameof(total), "Total must be greater than or equal to the sum of the detailed token counters.");

        Input = input;
        Output = output;
        CacheCreation = cacheCreation;
        CacheRead = cacheRead;
        Total = total;
    }
}

/// <summary>
/// Usage for a single model within an agent. <see cref="CostUsd"/> is <c>null</c> when the price
/// is unknown.
/// </summary>
public sealed record ModelUsage
{
    public string Model { get; }
    public TokenCounts Tokens { get; }
    public decimal? CostUsd { get; }

    public ModelUsage(string model, TokenCounts tokens, decimal? costUsd)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model must not be blank.", nameof(model));

        Model = model;
        Tokens = tokens;
        CostUsd = costUsd;
    }
}

/// <summary>
/// Usage for a single agent, optionally broken down by model.
/// </summary>
public sealed record AgentUsage
{
    public string Agent { get; }
    public TokenCounts Tokens { get; }
    public IReadOnlyList<ModelUsage> Models { get; }

    public AgentUsage(string agent, TokenCounts tokens, IReadOnlyList<ModelUsage> models)
    {
        if (string.IsNullOrWhiteSpace(agent))
            throw new ArgumentException("Agent must not be blank.", nameof(agent));
        if (models is null) throw new ArgumentNullException(nameof(models));

        Agent = agent.Trim().ToLowerInvariant();
        Tokens = tokens;
        Models = models;
    }

    /// <summary>
    /// Sum of model costs for which a price is known.
    /// </summary>
    public decimal PricedCostUsd
    {
        get
        {
            decimal sum = 0;
            foreach (var model in Models)
            {
                if (model.CostUsd.HasValue) sum += model.CostUsd.Value;
            }

            return sum;
        }
    }

    /// <summary>
    /// <c>true</c> when at least one model has an unknown price.
    /// </summary>
    public bool HasUnpricedModels
    {
        get
        {
            foreach (var model in Models)
            {
                if (!model.CostUsd.HasValue) return true;
            }

            return false;
        }
    }
}

/// <summary>
/// Daily usage grouped by agent.
/// </summary>
public sealed record DailyAgentUsage
{
    public DateOnly Date { get; }
    public IReadOnlyList<AgentUsage> Agents { get; }

    public DailyAgentUsage(DateOnly date, IReadOnlyList<AgentUsage> agents)
    {
        Agents = agents ?? throw new ArgumentNullException(nameof(agents));
        Date = date;
    }
}

/// <summary>
/// Monthly usage grouped by agent.
/// </summary>
public sealed record MonthlyAgentUsage
{
    public int Year { get; }
    public int Month { get; }
    public IReadOnlyList<AgentUsage> Agents { get; }

    public MonthlyAgentUsage(int year, int month, IReadOnlyList<AgentUsage> agents)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        Agents = agents ?? throw new ArgumentNullException(nameof(agents));
        Year = year;
        Month = month;
    }
}

/// <summary>
/// Usage for a single session.
/// </summary>
public sealed record SessionAgentUsage
{
    public string SessionId { get; }
    public DateTimeOffset LastActivity { get; }
    public AgentUsage Usage { get; }

    public SessionAgentUsage(string sessionId, DateTimeOffset lastActivity, AgentUsage usage)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId must not be blank.", nameof(sessionId));
        if (sessionId.Length > 256)
            throw new ArgumentException("SessionId must not exceed 256 characters.", nameof(sessionId));
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));

        SessionId = sessionId;
        LastActivity = lastActivity;
    }
}

/// <summary>
/// A complete ccusage report for daily, monthly and session granularity.
/// </summary>
public sealed record AgentUsageReport
{
    public IReadOnlyList<DailyAgentUsage> Daily { get; }
    public IReadOnlyList<MonthlyAgentUsage> Monthly { get; }
    public IReadOnlyList<SessionAgentUsage> Sessions { get; }
    public IReadOnlyList<string> UnpricedModels { get; }
    public AgentUsagePricing Pricing { get; }
    public DateTimeOffset RetrievedAt { get; }
    public int SkippedRows { get; }

    public AgentUsageReport(
        IReadOnlyList<DailyAgentUsage> daily,
        IReadOnlyList<MonthlyAgentUsage> monthly,
        IReadOnlyList<SessionAgentUsage> sessions,
        IReadOnlyList<string> unpricedModels,
        AgentUsagePricing pricing,
        DateTimeOffset retrievedAt,
        int skippedRows)
    {
        if (daily is null) throw new ArgumentNullException(nameof(daily));
        if (monthly is null) throw new ArgumentNullException(nameof(monthly));
        if (sessions is null) throw new ArgumentNullException(nameof(sessions));
        if (unpricedModels is null) throw new ArgumentNullException(nameof(unpricedModels));
        if (skippedRows < 0) throw new ArgumentOutOfRangeException(nameof(skippedRows));

        Daily = daily.OrderBy(d => d.Date).ToList().AsReadOnly();
        Monthly = monthly.OrderBy(m => m.Year).ThenBy(m => m.Month).ToList().AsReadOnly();
        Sessions = sessions.OrderByDescending(s => s.LastActivity).ToList().AsReadOnly();
        UnpricedModels = unpricedModels
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

        Pricing = pricing;
        RetrievedAt = retrievedAt;
        SkippedRows = skippedRows;
    }
}
