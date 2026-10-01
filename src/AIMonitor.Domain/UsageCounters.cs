namespace AIMonitor.Domain;

/// <summary>
/// Folded token/cost totals for one (day, model) or (day, project) cell, or for a whole range.
/// Immutable so an aggregator combines cells with <see cref="Combine"/> rather than mutating shared
/// state; a caller folding many transcript records does
/// <c>bucket = bucket.Combine(UsageCounters.ForMessage(...))</c> per record.
/// </summary>
public sealed record UsageCounters
{
    public int Messages { get; }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public long CacheWriteTokens { get; }

    public long CacheReadTokens { get; }

    public decimal CostUsd { get; }

    /// <summary>Tokens from models with no published price (<see cref="PricingKind.Unknown"/>). Counted
    /// here and left out of <see cref="CostUsd"/>, so the equivalent-value figure can say how much it
    /// does not know about instead of quietly adding zero for them.</summary>
    public long UnpricedTokens { get; }

    /// <summary>Tokens priced at their family's published rate (<see cref="PricingKind.Estimated"/>)
    /// because the exact model id has none. These *are* in <see cref="CostUsd"/>, but on a guess.</summary>
    public long EstimatedTokens { get; }

    /// <summary>Checked so a total that would silently wrap past <see cref="long.MaxValue"/> throws
    /// <see cref="OverflowException"/> instead of returning a corrupted (and typically negative) value.</summary>
    public long TotalTokens => checked(InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens);

    /// <summary>Share of cacheable input tokens that were served from cache; zero when nothing cacheable
    /// has been seen yet, rather than dividing by zero.</summary>
    public double CacheHitRate
    {
        get
        {
            var cacheable = CacheReadTokens + CacheWriteTokens + InputTokens;
            return cacheable == 0 ? 0.0 : (double)CacheReadTokens / cacheable;
        }
    }

    public static UsageCounters Zero { get; } = new();

    public UsageCounters(
        int messages = 0,
        long inputTokens = 0,
        long outputTokens = 0,
        long cacheWriteTokens = 0,
        long cacheReadTokens = 0,
        decimal costUsd = 0m,
        long unpricedTokens = 0,
        long estimatedTokens = 0)
    {
        Messages = RequireNonNegative(messages, nameof(messages));
        InputTokens = RequireNonNegative(inputTokens, nameof(inputTokens));
        OutputTokens = RequireNonNegative(outputTokens, nameof(outputTokens));
        CacheWriteTokens = RequireNonNegative(cacheWriteTokens, nameof(cacheWriteTokens));
        CacheReadTokens = RequireNonNegative(cacheReadTokens, nameof(cacheReadTokens));

        if (costUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(costUsd), costUsd, "Cost cannot be negative.");
        }

        CostUsd = costUsd;
        UnpricedTokens = RequireNonNegative(unpricedTokens, nameof(unpricedTokens));
        EstimatedTokens = RequireNonNegative(estimatedTokens, nameof(estimatedTokens));
    }

    /// <summary>The counters for a single folded message, ready to <see cref="Combine"/> into a bucket.</summary>
    public static UsageCounters ForMessage(
        long inputTokens,
        long outputTokens,
        long cacheWriteTokens,
        long cacheReadTokens,
        decimal costUsd,
        long unpricedTokens = 0,
        long estimatedTokens = 0) =>
        new(1, inputTokens, outputTokens, cacheWriteTokens, cacheReadTokens, costUsd, unpricedTokens, estimatedTokens);

    /// <summary>Checked so two operands that would sum past a field's representable range throw
    /// <see cref="OverflowException"/> explicitly, rather than wrapping silently and either corrupting
    /// the combined total or surfacing as a misleading "cannot be negative" error from the constructor.</summary>
    public UsageCounters Combine(UsageCounters other)
    {
        ArgumentNullException.ThrowIfNull(other);

        checked
        {
            return new UsageCounters(
                Messages + other.Messages,
                InputTokens + other.InputTokens,
                OutputTokens + other.OutputTokens,
                CacheWriteTokens + other.CacheWriteTokens,
                CacheReadTokens + other.CacheReadTokens,
                CostUsd + other.CostUsd,
                UnpricedTokens + other.UnpricedTokens,
                EstimatedTokens + other.EstimatedTokens);
        }
    }

    private static int RequireNonNegative(int value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value cannot be negative.");
        }

        return value;
    }

    private static long RequireNonNegative(long value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value cannot be negative.");
        }

        return value;
    }
}
