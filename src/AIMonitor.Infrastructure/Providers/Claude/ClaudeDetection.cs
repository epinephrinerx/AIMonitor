using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>Maps a <see cref="ClaudeCredentialReadResult"/> to the provider-neutral <see cref="DetectionInfo"/>
/// contract, using the fixed provider/source identity Claude Code detection always reports.</summary>
public static class ClaudeDetection
{
    public const string ProviderId = "claude";
    public const string SourceId = "claude_code";
    public const string SourceLabel = "Claude Code login";
    public const string RefreshHint = "Run `claude` in a terminal to refresh the login.";

    /// <summary>
    /// <c>subscriptionType</c> comes straight from the credentials file, which this app never wrote
    /// and does not fully trust — a corrupted or tampered file could stuff the access token or other
    /// arbitrary text into it. Only these known Claude plan labels are allowed to surface in
    /// <see cref="DetectionInfo.Account"/>; anything else maps to an empty account rather than
    /// echoing untrusted metadata.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownPlanLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["free"] = "Free plan",
            ["pro"] = "Pro plan",
            ["team"] = "Team plan",
            ["enterprise"] = "Enterprise plan",
            ["max"] = "Max plan",
        };

    public static DetectionInfo Detect(ClaudeCredentialReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            ClaudeCredentialStatus.Found => Candidate(result.Credentials!, DetectionState.Connected, hint: ""),
            ClaudeCredentialStatus.Expired => Candidate(result.Credentials!, DetectionState.Expired, RefreshHint),
            _ => new DetectionInfo(ProviderId, DetectionState.NotConnected),
        };
    }

    private static DetectionInfo Candidate(ClaudeCredentials credentials, DetectionState state, string hint) =>
        new(
            ProviderId,
            state,
            SourceId,
            SourceLabel,
            account: PlanAccount(credentials.SubscriptionType),
            hint: hint,
            candidates: [new DetectionCandidate(SourceId, SourceLabel)]);

    /// <summary>Matches the Python baseline's <c>f"{plan.title()} plan"</c> for known plans; empty
    /// when there is no subscription type or it is not one of the conservative known values, never
    /// an invented or attacker-controlled plan name.</summary>
    private static string PlanAccount(string? subscriptionType) =>
        subscriptionType is not null && KnownPlanLabels.TryGetValue(subscriptionType, out var label)
            ? label
            : string.Empty;
}
