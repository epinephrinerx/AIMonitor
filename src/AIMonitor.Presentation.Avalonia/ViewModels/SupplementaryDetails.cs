using System.Globalization;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>One line of the supplementary section: plan, credits or limit reason, with its badge.</summary>
public sealed class SupplementaryRowViewModel
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Group { get; init; } = "";

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>A small "Label: value" chip in the activity part of the section.</summary>
public sealed class SupplementaryPillViewModel
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string DisplayText => string.IsNullOrEmpty(Label) ? Value : $"{Label}: {Value}";
}

/// <summary>
/// The "Supplementary details" section under Claude's and Codex's gauges, as on Windows (R-007,
/// R-009, R-010, R-011): the plan, credits and limit reason the service reports (or that it does
/// not), the reset-rights card with its expandable how-to, and the activity pills.
/// </summary>
public sealed class SupplementaryDetailsViewModel : ObservableObject
{
    private bool _isResetDetailsExpanded;

    public string ProviderId { get; init; } = "";
    public string SectionTitle { get; init; } = "Supplementary Details & Telemetry";
    public string ResetTitle { get; init; } = "Rate Limit Resets";
    public string ResetBadge { get; init; } = "";
    public string ResetSummary { get; init; } = "";
    public string ResetDetails { get; init; } = "";
    public IReadOnlyList<SupplementaryRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<SupplementaryPillViewModel> ActivityPills { get; init; } = [];
    public string ActivityTitle { get; init; } = "Activity";
    public string ActivityNote { get; init; } = "";

    public bool HasRows => Rows.Count > 0;
    public bool HasActivity => ActivityPills.Count > 0 || !string.IsNullOrEmpty(ActivityNote);
    public bool HasActivityNote => ActivityNote.Length > 0;
    public bool HasResetBadge => ResetBadge.Length > 0;
    public bool HasResetDetails => ResetDetails.Length > 0;

    public bool HasResetInfo =>
        !string.IsNullOrEmpty(ResetTitle)
        || !string.IsNullOrEmpty(ResetBadge)
        || !string.IsNullOrEmpty(ResetSummary)
        || !string.IsNullOrEmpty(ResetDetails);

    public bool IsResetDetailsExpanded
    {
        get => _isResetDetailsExpanded;
        set => Set(ref _isResetDetailsExpanded, value);
    }
}

/// <summary>Builds the supplementary section from a snapshot; the same rules as the Windows tab.</summary>
public static class SupplementaryDetailsBuilder
{
    /// <summary>Only Claude and Codex have the section.</summary>
    public static bool AppliesTo(string providerId) =>
        string.Equals(providerId, "claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals(providerId, "openai", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupplementaryStat(Stat s)
    {
        var label = s.Label;
        if (label.Equals("Plan", StringComparison.OrdinalIgnoreCase) || label.EndsWith("· Plan", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.Equals("Credits", StringComparison.OrdinalIgnoreCase) || label.EndsWith("· Credits", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.Equals("Limit reason", StringComparison.OrdinalIgnoreCase) || label.EndsWith("· Limit reason", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.StartsWith("Rate limit reset", StringComparison.OrdinalIgnoreCase))
            return true;
        if (label.Equals("Current streak", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Longest streak", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Longest turn", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static SupplementaryDetailsViewModel Build(string providerId, ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var isClaude = string.Equals(providerId, "claude", StringComparison.OrdinalIgnoreCase);
        var isOpenAi = string.Equals(providerId, "openai", StringComparison.OrdinalIgnoreCase);
        string resetTitle;
        string resetBadge;
        string resetSummary;
        string resetDetails;

        if (isClaude)
        {
            resetTitle = "Limit Resets (Claude)";
            resetBadge = "Not verified";
            resetSummary = "AIMonitor cannot verify reset offers here. Check Settings > Usage on Claude web/Desktop.";
            resetDetails = "Official Claude Limit Reset Procedure\n\n"
                + "Claude limit resets are managed directly within the official Anthropic interface:\n"
                + "1. Open Claude on Web (claude.ai) or Claude Desktop (not mobile app, IDE extension, or terminal).\n"
                + "2. Navigate to Settings > Usage.\n"
                + "3. Under Resets, check if an offer is present.\n"
                + "4. Click Reset for free > Confirm.\n\n"
                + "Rules: Redemption is irreversible. Normal weekly schedule remains unchanged. Extra usage credits are distinct. Shared account benefits Claude Code and mobile.\n\n"
                + "Reference: support.claude.com/en/articles/17007452";
        }
        else
        {
            resetTitle = "Rate Limit Resets (Codex)";
            var resetStat = snapshot.Stats.FirstOrDefault(s => s.Label.StartsWith("Rate limit reset", StringComparison.OrdinalIgnoreCase));
            if (resetStat is not null)
            {
                if (long.TryParse(resetStat.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0)
                {
                    if (count > 0)
                    {
                        resetBadge = $"Available: {count}";
                        var expiry = !string.IsNullOrEmpty(resetStat.Detail) ? resetStat.Detail : "Next expiry unknown";
                        resetSummary = $"{count} reset rights available · {expiry}. These are separate from paid credits.";
                    }
                    else
                    {
                        resetBadge = "0 Credits";
                        resetSummary = "0 Rate Limit Reset Credits · Confirmed no reset credits available on this account (count = 0).";
                    }

                    resetDetails = "Codex Reset Details\n\n"
                        + "Earned reset rights are reported via Codex App Server account/rateLimits/read. When availableCount > 0, users may restore a capped quota window.\n\n"
                        + "Read-Only Notice: AIMonitor is a read-only monitor. Reset redemption is managed via official client. No redemption action is executed here.\n\n"
                        + "Reference: learn.chatgpt.com/docs/app-server";
                }
                else
                {
                    resetBadge = "Not verified";
                    resetSummary = "Reset Credits Status: Not verified · Server returned malformed or unconfirmed status.";
                    resetDetails = "Codex Reset Details\n\n"
                        + "Server returned malformed or unconfirmed status. Entitlement cannot be verified by AIMonitor.\n\n"
                        + "Read-Only Notice: AIMonitor is a read-only monitor. No redemption action is executed here.\n\n"
                        + "Reference: learn.chatgpt.com/docs/app-server";
                }
            }
            else
            {
                resetBadge = "Not verified";
                resetSummary = "Reset Credits Status: Not verified · No confirmed reset data (not zero). Entitlement cannot be verified by AIMonitor.";
                resetDetails = "Codex Reset Details\n\n"
                    + "No confirmed reset data (not zero). Entitlement cannot be verified by AIMonitor.\n\n"
                    + "Read-Only Notice: AIMonitor is a read-only monitor. No redemption action is executed here.\n\n"
                    + "Reference: learn.chatgpt.com/docs/app-server";
            }
        }

        var isApiMode = isOpenAi && snapshot.Detection?.SourceId is "manual" or "env";
        if (isApiMode)
        {
            resetTitle = "Codex reset rights";
            resetBadge = "Not available in API mode";
            resetSummary = "Organization API usage does not report ChatGPT/Codex reset rights.";
            resetDetails = "Use your Codex ChatGPT login to view earned reset rights. AIMonitor performs no redemption.";
        }

        var rows = new List<SupplementaryRowViewModel>();

        foreach (var s in snapshot.Stats)
        {
            if (s.Label.Equals("Plan", StringComparison.OrdinalIgnoreCase) || s.Label.EndsWith("· Plan", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = s.Label,
                    Value = s.Value,
                    Detail = s.Detail,
                    Badge = "Plan",
                    Group = "Plan"
                });
            }
            else if (s.Label.Equals("Credits", StringComparison.OrdinalIgnoreCase) || s.Label.EndsWith("· Credits", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = s.Label,
                    Value = s.Value,
                    Detail = s.Detail,
                    Badge = "Credits",
                    Group = "Credits"
                });
            }
            else if (s.Label.Equals("Limit reason", StringComparison.OrdinalIgnoreCase) || s.Label.EndsWith("· Limit reason", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = s.Label,
                    Value = s.Value,
                    Detail = s.Detail,
                    Badge = "Limit",
                    Group = "Limit reason"
                });
            }
        }

        if (!rows.Any(r => r.Group == "Plan"))
        {
            if (isClaude)
            {
                rows.Insert(0, new SupplementaryRowViewModel
                {
                    Label = "Plan",
                    Value = "Not reported",
                    Detail = "Check your plan in Claude",
                    Badge = "Plan",
                    Group = "Plan"
                });
            }
            else if (isOpenAi)
            {
                rows.Insert(0, new SupplementaryRowViewModel
                {
                    Label = "Plan",
                    Value = "Not reported",
                    Detail = "Plan not reported via API",
                    Badge = "Plan",
                    Group = "Plan"
                });
            }
        }

        if (!rows.Any(r => r.Group == "Credits"))
        {
            if (isClaude)
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = "Workspace Credits",
                    Value = "Not verified",
                    Detail = "Check Claude for extra usage balance",
                    Badge = "Credits",
                    Group = "Credits"
                });
            }
            else if (isOpenAi)
            {
                rows.Add(new SupplementaryRowViewModel
                {
                    Label = "Workspace Credits",
                    Value = "Not reported",
                    Detail = "Workspace credit balance unavailable",
                    Badge = "Credits",
                    Group = "Credits"
                });
            }
        }

        // Activity
        var activityPills = new List<SupplementaryPillViewModel>();
        string activityTitle;
        string activityNote;

        var currentStreak = snapshot.Stats.FirstOrDefault(s => s.Label.Equals("Current streak", StringComparison.OrdinalIgnoreCase));
        var longestStreak = snapshot.Stats.FirstOrDefault(s => s.Label.Equals("Longest streak", StringComparison.OrdinalIgnoreCase));
        var longestTurn = snapshot.Stats.FirstOrDefault(s => s.Label.Equals("Longest turn", StringComparison.OrdinalIgnoreCase));

        if (currentStreak is not null || longestStreak is not null || longestTurn is not null)
        {
            activityTitle = "Codex Activity";
            activityNote = "From account/usage/read summary · Metrics hide when null.";
            if (currentStreak is not null) activityPills.Add(new SupplementaryPillViewModel { Label = "Current streak", Value = currentStreak.Value });
            if (longestStreak is not null) activityPills.Add(new SupplementaryPillViewModel { Label = "Longest streak", Value = longestStreak.Value });
            if (longestTurn is not null) activityPills.Add(new SupplementaryPillViewModel { Label = "Longest turn", Value = longestTurn.Value });
        }
        else if (isClaude)
        {
            activityTitle = "Claude Telemetry Notes";
            activityNote = "Resets apply to 5-hour session or weekly quota as indicated by offer. Check Settings > Usage in Claude.";
            activityPills.Add(new SupplementaryPillViewModel { Label = "Scope", Value = "Session / Weekly" });
            activityPills.Add(new SupplementaryPillViewModel { Label = "Cross-client", Value = "Web / Desktop / Code" });
        }
        else
        {
            activityTitle = "Codex Activity";
            activityNote = "Activity telemetry not reported";
        }

        if (isApiMode)
        {
            activityTitle = "Account activity";
            activityNote = "Codex activity is not available in organization API mode.";
        }

        return new SupplementaryDetailsViewModel
        {
            ProviderId = providerId,
            ResetTitle = resetTitle,
            ResetBadge = resetBadge,
            ResetSummary = resetSummary,
            ResetDetails = resetDetails,
            Rows = rows,
            ActivityPills = activityPills,
            ActivityTitle = activityTitle,
            ActivityNote = activityNote,
        };
    }
}
