namespace AIMonitor.Domain;

/// <summary>The metrics a usage chart or breakdown can be drawn against.</summary>
public static class UsageMetric
{
    public const string TotalTokens = "Total tokens";
    public const string OutputTokens = "Output tokens";
    public const string EquivalentValue = "Equivalent value";

    /// <summary>The scalar <paramref name="counters"/> represents under <paramref name="metric"/>.
    /// Anything other than <see cref="OutputTokens"/> or <see cref="EquivalentValue"/> - including an
    /// empty or unrecognised metric - reads as <see cref="TotalTokens"/>, matching the field's own
    /// documented default.</summary>
    public static double ValueOf(UsageCounters counters, string metric)
    {
        ArgumentNullException.ThrowIfNull(counters);

        return metric switch
        {
            OutputTokens => counters.OutputTokens,
            EquivalentValue => (double)counters.CostUsd,
            _ => counters.TotalTokens,
        };
    }
}
