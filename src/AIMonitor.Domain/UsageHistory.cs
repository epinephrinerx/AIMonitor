namespace AIMonitor.Domain;

/// <summary>One local day's usage, broken down by model. Ingestion/aggregation is out of scope
/// for this slice; this type only carries whatever a caller has already computed.</summary>
public sealed record UsageHistoryBucket
{
    public DateOnly Day { get; }

    public IReadOnlyDictionary<string, double> PerModel { get; }

    public double Total => PerModel.Values.Sum();

    public UsageHistoryBucket(DateOnly day, IReadOnlyDictionary<string, double>? perModel = null)
    {
        Day = day;
        PerModel = DomainGuard.ToReadOnlyCopy(perModel);
        foreach (var (model, value) in PerModel)
        {
            DomainGuard.RequireNonBlank(model, nameof(perModel));
            RequireFiniteNonNegative(value, nameof(perModel));
        }
    }

    internal static void RequireFiniteNonNegative(double value, string paramName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be a finite value that is zero or greater.");
        }
    }
}

/// <summary>
/// One day's priced cost for the optional chart cost line. <see cref="PricedUsd"/> only sums
/// models whose price is known; <see cref="HasUnpricedModels"/> says that at least one of the day's
/// models has no price, so the money value is incomplete rather than zero.
/// </summary>
public sealed record DailyCost
{
    public DateOnly Day { get; }

    public double PricedUsd { get; }

    public bool HasUnpricedModels { get; }

    public DailyCost(DateOnly day, double pricedUsd, bool hasUnpricedModels)
    {
        UsageHistoryBucket.RequireFiniteNonNegative(pricedUsd, nameof(pricedUsd));
        Day = day;
        PricedUsd = pricedUsd;
        HasUnpricedModels = hasUnpricedModels;
    }
}

/// <summary>One entry in a by-model or by-project breakdown.</summary>
public sealed record UsageHistoryBreakdown
{
    public string Label { get; }

    public double Value { get; }

    public UsageHistoryBreakdown(string label, double value)
    {
        Label = DomainGuard.RequireNonBlank(label, nameof(label));
        UsageHistoryBucket.RequireFiniteNonNegative(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// A provider-neutral, chart-agnostic history view: the day buckets for the selected range and
/// metric, plus the by-model and by-project breakdowns the baseline chart needs. Ingestion and
/// aggregation are out of scope for this slice; this type only carries whatever a caller has
/// already computed.
/// </summary>
public sealed record UsageHistory
{
    public IReadOnlyList<UsageHistoryBucket> Buckets { get; }

    public IReadOnlyList<string> Series { get; }

    public IReadOnlyList<UsageHistoryBreakdown> ByModel { get; }

    public IReadOnlyList<UsageHistoryBreakdown> ByProject { get; }

    /// <summary>Optional per-day priced costs for the chart's cost line; empty when not provided.</summary>
    public IReadOnlyList<DailyCost> DailyCosts { get; }

    public int Days { get; }

    public string Metric { get; }

    public string ProjectLabel { get; }

    public UsageHistory(
        IReadOnlyList<UsageHistoryBucket>? buckets = null,
        IReadOnlyList<string>? series = null,
        IReadOnlyList<UsageHistoryBreakdown>? byModel = null,
        IReadOnlyList<UsageHistoryBreakdown>? byProject = null,
        int days = 14,
        string metric = "Total tokens",
        string projectLabel = "By project",
        IReadOnlyList<DailyCost>? dailyCosts = null)
    {
        Buckets = DomainGuard.ToReadOnlyCopy(buckets);
        Series = DomainGuard.ToReadOnlyCopy(series);
        foreach (var label in Series)
        {
            DomainGuard.RequireNonBlank(label, nameof(series));
        }

        ByModel = DomainGuard.ToReadOnlyCopy(byModel);
        ByProject = DomainGuard.ToReadOnlyCopy(byProject);
        DailyCosts = DomainGuard.ToReadOnlyCopy(dailyCosts);

        if (days <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "Days must be greater than zero.");
        }

        Days = days;
        Metric = DomainGuard.RequireNonBlank(metric, nameof(metric));
        ProjectLabel = DomainGuard.RequireNonBlank(projectLabel, nameof(projectLabel));
    }
}
