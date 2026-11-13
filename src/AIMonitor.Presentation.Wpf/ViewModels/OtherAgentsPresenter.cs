using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed record OtherAgentRow(string DisplayName, string AgentId, string Guidance);
public sealed record OtherAgentsPresentation(string Status, IReadOnlyList<OtherAgentRow> Rows);

public static class OtherAgentsPresenter
{
    private const string Generic = "Sign in with this agent's own app or CLI as its documentation describes. AIMonitor only shows the usage ccusage finds on this machine; it never signs in for you or reads this agent's credentials.";
    public static OtherAgentsPresentation Build(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError)
    {
        var excluded = new HashSet<string>(["claude", "codex", "antigravity", "opencode"], StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> ids = lastGood is null ? Enumerable.Empty<string>() : lastGood.Daily.SelectMany(d => d.Agents.Select(a => a.Agent))
            .Concat(lastGood.Monthly.SelectMany(m => m.Agents.Select(a => a.Agent)))
            .Concat(lastGood.Sessions.Select(s => s.Usage.Agent)).Where(id => !excluded.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = ids.Select(id => new OtherAgentRow(AgentUsagePresenter.AgentDisplayName(id), id, id.ToLowerInvariant() switch
        {
            "copilot" => "Run `copilot login` in a terminal (or /login inside GitHub Copilot CLI) and finish the browser or device-code sign-in. A fine-grained personal access token with the \"Copilot Requests\" permission in COPILOT_GITHUB_TOKEN, GH_TOKEN or GITHUB_TOKEN also works.",
            "zcode" => "Open the ZCode desktop app, click Connect at the bottom left and sign in with Z.ai or BigModel, or add an API key under Settings → Model Providers.",
            _ => Generic
        })).OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.AgentId, StringComparer.Ordinal).ToList();
        var status = lastGood is null
            ? lastError is null ? "Checking ccusage…" : "ccusage is not available, so other agents cannot be listed."
            : rows.Count == 0 ? "No other agents found in ccusage data on this machine." : "Agents found by ccusage that have no tab of their own:";
        return new(status, rows);
    }
}
