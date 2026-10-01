namespace AIMonitor.Domain;

/// <summary>
/// Combines the local usage thresholds with whatever severity the server reported, always
/// choosing the more severe of the two so a low percentage never masks a server-side lock and a
/// high percentage still colours even when the server calls it normal.
/// </summary>
public static class SeverityRules
{
    public const double HighThreshold = 75.0;
    public const double CriticalThreshold = 90.0;

    /// <summary>The severity implied by the percentage alone. Absent data implies nothing.</summary>
    public static Severity LocalSeverity(double? percent)
    {
        if (percent is not double value)
        {
            return Severity.Normal;
        }

        if (value >= CriticalThreshold)
        {
            return Severity.Critical;
        }

        return value >= HighThreshold ? Severity.High : Severity.Normal;
    }

    /// <summary>The effective severity: the worse of the local threshold and the server's own.</summary>
    public static Severity Combine(double? percent, Severity serverSeverity)
    {
        DomainGuard.RequireDefined(serverSeverity, nameof(serverSeverity));

        var local = LocalSeverity(percent);
        return serverSeverity > local ? serverSeverity : local;
    }
}
