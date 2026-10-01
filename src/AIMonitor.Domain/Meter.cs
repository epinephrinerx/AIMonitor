namespace AIMonitor.Domain;

/// <summary>
/// A usage window such as a session or weekly quota. <see cref="Percent"/> is <see langword="null"/>
/// when the service exposes no denominator; no percentage is ever invented for it.
/// </summary>
public sealed record Meter
{
    public string Kind { get; }

    /// <summary>Stable identity for this meter, distinct from <see cref="Kind"/> when a provider
    /// exposes more than one window of the same kind (e.g. several per-model weekly windows).
    /// Required and never defaulted from <see cref="Kind"/>, because doing so would let two
    /// same-kind windows collide under one ambiguous identity.</summary>
    public string Key { get; }

    /// <summary>Semantic grouping used for reading order. Falls back to <see cref="Kind"/> so a
    /// server-reported group of "session" can promote a window to the front even when its own
    /// <see cref="Kind"/> is not literally "session".</summary>
    public string Group { get; }

    public string Title { get; }

    /// <summary>May be empty (a provider legitimately has nothing to say here), but never null.</summary>
    public string Subtitle { get; }

    public double? Percent { get; }

    public Severity ServerSeverity { get; }

    public DateTimeOffset? ResetsAt { get; }

    public string Detail { get; }

    public string? LockedReason { get; }

    public Meter(
        string kind,
        string key,
        string title,
        string subtitle,
        double? percent = null,
        Severity serverSeverity = Severity.Normal,
        DateTimeOffset? resetsAt = null,
        string detail = "",
        string? lockedReason = null,
        string? group = null)
    {
        Kind = DomainGuard.RequireNonBlank(kind, nameof(kind));
        Key = DomainGuard.RequireNonBlank(key, nameof(key));
        Group = string.IsNullOrWhiteSpace(group) ? Kind : group;
        Title = DomainGuard.RequireNonBlank(title, nameof(title));
        ArgumentNullException.ThrowIfNull(subtitle);
        Subtitle = subtitle;

        if (percent is double value && (double.IsNaN(value) || double.IsInfinity(value) || value < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(percent), value, "Percent must be a finite value that is zero or greater.");
        }

        Percent = percent;
        ServerSeverity = DomainGuard.RequireDefined(serverSeverity, nameof(serverSeverity));
        ResetsAt = resetsAt;
        Detail = detail ?? string.Empty;
        LockedReason = lockedReason;
    }

    /// <summary>The severity to display: the local threshold and the server's own, whichever is worse.</summary>
    public Severity EffectiveSeverity => SeverityRules.Combine(Percent, ServerSeverity);
}
