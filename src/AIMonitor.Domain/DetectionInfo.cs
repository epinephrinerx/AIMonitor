namespace AIMonitor.Domain;

/// <summary>Connection state for a detected login, in the vocabulary the UI renders.</summary>
public enum DetectionState
{
    NotConnected,
    Connected,
    Limited,
    Expired,
}

/// <summary>One source that yielded a credential while resolving a provider's login, kept so the
/// connections page can show every source that yielded a credential, not just the one that won.
/// Expired or limited sources still count here, matching the Python baseline.</summary>
public sealed record DetectionCandidate
{
    public string SourceId { get; }

    public string SourceLabel { get; }

    public DetectionCandidate(string sourceId, string sourceLabel)
    {
        SourceId = DomainGuard.RequireNonBlank(sourceId, nameof(sourceId));
        SourceLabel = sourceLabel ?? string.Empty;
    }
}

/// <summary>What was found for one provider's login. Resolution logic is out of scope for this
/// slice; this type only carries the outcome.</summary>
public sealed record DetectionInfo
{
    public string ProviderId { get; }

    public DetectionState State { get; }

    public string SourceId { get; }

    public string SourceLabel { get; }

    public string Account { get; }

    /// <summary>Why the state is what it is, e.g. a refresh command or the reason usage cannot be
    /// read from this login. Empty when the state needs no explanation.</summary>
    public string Hint { get; }

    public IReadOnlyList<DetectionCandidate> Candidates { get; }

    public bool Usable => State == DetectionState.Connected;

    public DetectionInfo(
        string providerId,
        DetectionState state = DetectionState.NotConnected,
        string sourceId = "",
        string sourceLabel = "",
        string account = "",
        string hint = "",
        IReadOnlyList<DetectionCandidate>? candidates = null)
    {
        ProviderId = DomainGuard.RequireNonBlank(providerId, nameof(providerId));
        State = DomainGuard.RequireDefined(state, nameof(state));
        SourceId = sourceId ?? string.Empty;
        SourceLabel = sourceLabel ?? string.Empty;
        Account = account ?? string.Empty;
        Hint = hint ?? string.Empty;
        Candidates = DomainGuard.ToReadOnlyCopy(candidates);
    }
}
