using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-021 G1-G3 (Docs/SPEC_R020_R021.md) through the spec's own seam
/// <c>ConnectionsViewModel.UpdateOtherAgents(lastGood, lastError)</c> -> <c>OtherAgents</c> /
/// <c>OtherAgentsStatus</c> (the pure OtherAgentsPresenter has no spec-given method name, so it is exercised
/// through the view model that uses it). Rows are <c>(DisplayName, AgentId, Guidance)</c>.
/// Oracles: display names come from <c>AgentUsagePresenter.AgentDisplayName</c> (copilot -> "Copilot",
/// zcode -> "ZCode", unknown ids show the raw id); order is DisplayName OrdinalIgnoreCase.
/// </summary>
public sealed class ConnectionsViewModelOtherAgentsTests
{
    // G2, verbatim ("→" is U+2192, the quotes around Copilot Requests are plain double quotes).
    private const string CopilotGuidance =
        "Run `copilot login` in a terminal (or /login inside GitHub Copilot CLI) and finish the browser or device-code sign-in. A fine-grained personal access token with the \"Copilot Requests\" permission in COPILOT_GITHUB_TOKEN, GH_TOKEN or GITHUB_TOKEN also works.";

    private const string ZCodeGuidance =
        "Open the ZCode desktop app, click Connect at the bottom left and sign in with Z.ai or BigModel, or add an API key under Settings → Model Providers.";

    private const string GenericGuidance =
        "Sign in with this agent's own app or CLI as its documentation describes. AIMonitor only shows the usage ccusage finds on this machine; it never signs in for you or reads this agent's credentials.";

    // G3, verbatim ("…" is U+2026).
    private const string CheckingStatus = "Checking ccusage…";
    private const string UnavailableStatus = "ccusage is not available, so other agents cannot be listed.";
    private const string NoneStatus = "No other agents found in ccusage data on this machine.";
    private const string FoundStatus = "Agents found by ccusage that have no tab of their own:";

    private static readonly DateOnly Day = new(2026, 11, 9);
    private static readonly DateTimeOffset Stamp = new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero);

    private static ConnectionsViewModel CreateViewModel()
    {
        var settings = new AppSettings();
        var store = new BlockingSettingsStore(settings);
        store.Release();
        return new ConnectionsViewModel(new SettingsSession(store, settings));
    }

    private static AgentUsage Agent(string id) =>
        new(id, new TokenCounts(10, 0, 0, 0, 10), []);

    private static AgentUsageReport Report(
        string[]? daily = null,
        string[]? monthly = null,
        string[]? sessions = null) =>
        new(
            daily: daily is null ? [] : [new DailyAgentUsage(Day, daily.Select(Agent).ToList())],
            monthly: monthly is null ? [] : [new MonthlyAgentUsage(Day.Year, Day.Month, monthly.Select(Agent).ToList())],
            sessions: sessions is null ? [] : sessions.Select((id, i) => new SessionAgentUsage("s" + i, Stamp.AddMinutes(i), Agent(id))).ToList(),
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: Stamp,
            skippedRows: 0);

    private static AgentUsageUnavailableException Error(string message = "ccusage could not read usage.") =>
        new(AgentUsageErrorKind.Failed, message);

    private static string[] Ids(ConnectionsViewModel vm) => vm.OtherAgents.Select(r => r.AgentId).ToArray();

    // ── G1: which agents, dedupe, order ─────────────────────────────────────────

    [Fact]
    public void ExcludesClaudeCodexAntigravityAndOpenCode_FromEveryPeriod_G1()
    {
        var vm = CreateViewModel();
        var report = Report(
            daily: ["claude", "codex", "copilot"],
            monthly: ["antigravity", "opencode", "zcode"],
            sessions: ["claude", "codex", "antigravity", "opencode", "example-agent"]);

        vm.UpdateOtherAgents(report, null);

        Assert.Equal(new[] { "copilot", "example-agent", "zcode" }, Ids(vm));
    }

    [Fact]
    public void CollectsAgentsFromDailyMonthlyAndSessions_EachAlone_G1()
    {
        var vmDaily = CreateViewModel();
        vmDaily.UpdateOtherAgents(Report(daily: ["amp"]), null);
        Assert.Equal(new[] { "amp" }, Ids(vmDaily));

        var vmMonthly = CreateViewModel();
        vmMonthly.UpdateOtherAgents(Report(monthly: ["goose"]), null);
        Assert.Equal(new[] { "goose" }, Ids(vmMonthly));

        var vmSessions = CreateViewModel();
        vmSessions.UpdateOtherAgents(Report(sessions: ["kimi"]), null);
        Assert.Equal(new[] { "kimi" }, Ids(vmSessions));
    }

    [Fact]
    public void DistinctIds_AcrossPeriods_AppearOnce_CaseInsensitive_G1()
    {
        var vm = CreateViewModel();
        var report = Report(
            daily: ["copilot", "Copilot"],
            monthly: ["COPILOT", "zcode"],
            sessions: ["ZCode", "copilot", "zcode"]);

        vm.UpdateOtherAgents(report, null);

        Assert.Equal(2, vm.OtherAgents.Count);
        Assert.Equal(new[] { "copilot", "zcode" }, vm.OtherAgents.Select(r => r.AgentId.ToLowerInvariant()));
    }

    [Fact]
    public void Rows_AreOrderedByDisplayName_OrdinalIgnoreCase_G1()
    {
        // Display names: amp, Copilot, example-agent, goose, ZCode -> a mixed-case collation would put
        // "ZCode" before "amp"; OrdinalIgnoreCase keeps it last.
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["zcode", "goose", "example-agent", "copilot", "amp"]), null);

        Assert.Equal(new[] { "amp", "Copilot", "example-agent", "goose", "ZCode" }, vm.OtherAgents.Select(r => r.DisplayName));
        Assert.Equal(new[] { "amp", "copilot", "example-agent", "goose", "zcode" }, Ids(vm));
    }

    [Fact]
    public void DisplayName_IsAgentUsagePresenterName_AndUnknownIdsShowTheRawId_G1_G2()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["copilot", "zcode", "example-agent"]), null);

        var byId = vm.OtherAgents.ToDictionary(r => r.AgentId);
        Assert.Equal(AgentUsagePresenter.AgentDisplayName("copilot"), byId["copilot"].DisplayName);
        Assert.Equal("Copilot", byId["copilot"].DisplayName);
        Assert.Equal("ZCode", byId["zcode"].DisplayName);
        Assert.Equal("example-agent", byId["example-agent"].DisplayName);
    }

    // ── G2: guidance, verbatim ──────────────────────────────────────────────────

    [Fact]
    public void Guidance_CopilotZCodeAndGeneric_AreVerbatim_G2()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["copilot", "zcode", "example-agent"]), null);

        var byId = vm.OtherAgents.ToDictionary(r => r.AgentId);
        Assert.Equal(CopilotGuidance, byId["copilot"].Guidance);
        Assert.Equal(ZCodeGuidance, byId["zcode"].Guidance);
        Assert.Equal(GenericGuidance, byId["example-agent"].Guidance);
    }

    [Theory]
    [InlineData("amp")]
    [InlineData("droid")]
    [InlineData("codebuff")]
    [InlineData("cline")]
    [InlineData("hermes")]
    [InlineData("pi")]
    [InlineData("goose")]
    [InlineData("openclaw")]
    [InlineData("kilo")]
    [InlineData("kimi")]
    [InlineData("qwen")]
    [InlineData("gemini")]
    [InlineData("grok")]
    [InlineData("some-future-agent")]
    public void EveryOtherId_KnownToCcusageOrUnknown_GetsTheGenericGuidance_G2(string id)
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: [id]), null);

        var row = Assert.Single(vm.OtherAgents);
        Assert.Equal(GenericGuidance, row.Guidance);
    }

    [Fact]
    public void GuidanceNeverNamesAnotherAgentsSecrets_AndOnlyCopilotMentionsTokens_G2()
    {
        // G2 wording for the generic text: AIMonitor "never signs in for you or reads this agent's credentials".
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["copilot", "zcode", "example-agent"]), null);

        foreach (var row in vm.OtherAgents.Where(r => r.AgentId != "copilot"))
        {
            Assert.DoesNotContain("GITHUB_TOKEN", row.Guidance, StringComparison.Ordinal);
            Assert.DoesNotContain("copilot login", row.Guidance, StringComparison.Ordinal);
        }
    }

    // ── G3: status texts ────────────────────────────────────────────────────────

    [Fact]
    public void BothNull_IsChecking_WithNoRows_G3()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(null, null);

        Assert.Equal(CheckingStatus, vm.OtherAgentsStatus);
        Assert.Empty(vm.OtherAgents);
    }

    [Fact]
    public void NoReportAndAnError_SaysCcusageIsNotAvailable_WithNoRows_AndNoExceptionText_G3()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(null, Error("SENTINEL exception text at C:\\secret\\path"));

        Assert.Equal(UnavailableStatus, vm.OtherAgentsStatus);
        Assert.Empty(vm.OtherAgents);
    }

    [Fact]
    public void ReportWithZeroOtherRows_SaysNoOtherAgentsFound_G3()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["claude", "codex", "antigravity", "opencode"]), null);

        Assert.Equal(NoneStatus, vm.OtherAgentsStatus);
        Assert.Empty(vm.OtherAgents);
    }

    [Fact]
    public void EmptyReport_SaysNoOtherAgentsFound_G3()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(), null);

        Assert.Equal(NoneStatus, vm.OtherAgentsStatus);
        Assert.Empty(vm.OtherAgents);
    }

    [Fact]
    public void ReportWithRows_SaysAgentsFound_G3()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["copilot"]), null);

        Assert.Equal(FoundStatus, vm.OtherAgentsStatus);
        Assert.Single(vm.OtherAgents);
    }

    [Fact]
    public void ReportWithRows_AndALaterError_KeepsRowsFromLastGood_AndTheFoundStatus_NoExceptionText_G3()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["copilot", "zcode"]), Error("SENTINEL refresh failed at 07:00"));

        Assert.Equal(FoundStatus, vm.OtherAgentsStatus);
        Assert.Equal(new[] { "copilot", "zcode" }, Ids(vm));
        Assert.DoesNotContain("SENTINEL", vm.OtherAgentsStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("07:00", vm.OtherAgentsStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportWithZeroRows_AndALaterError_SaysNoOtherAgentsFound_NotUnavailable_G3()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["claude"]), Error());

        Assert.Equal(NoneStatus, vm.OtherAgentsStatus);
        Assert.Empty(vm.OtherAgents);
    }

    [Fact]
    public void StatusAndRows_AreReplacedByEachUpdate_NothingIsRetained_G3()
    {
        var vm = CreateViewModel();
        vm.UpdateOtherAgents(Report(daily: ["copilot", "zcode"]), null);

        vm.UpdateOtherAgents(null, Error());
        Assert.Equal(UnavailableStatus, vm.OtherAgentsStatus);
        Assert.Empty(vm.OtherAgents);

        vm.UpdateOtherAgents(Report(daily: ["goose"]), null);
        Assert.Equal(FoundStatus, vm.OtherAgentsStatus);
        Assert.Equal(new[] { "goose" }, Ids(vm));

        vm.UpdateOtherAgents(null, null);
        Assert.Equal(CheckingStatus, vm.OtherAgentsStatus);
        Assert.Empty(vm.OtherAgents);
    }

    [Fact]
    public void UpdateOtherAgents_NotifiesTheViewOfStatusAndRowChanges_G4()
    {
        var vm = CreateViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var collectionChanged = false;
        if (vm.OtherAgents is System.Collections.Specialized.INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += (_, _) => collectionChanged = true;
        }

        vm.UpdateOtherAgents(Report(daily: ["copilot"]), null);

        Assert.Contains(nameof(ConnectionsViewModel.OtherAgentsStatus), changed);
        // The rows reach the page either by a replaced list (PropertyChanged) or by an observable collection.
        Assert.True(changed.Contains(nameof(ConnectionsViewModel.OtherAgents)) || collectionChanged);
    }

    [Fact]
    public void UpdateOtherAgents_DoesNotTouchTheProviderCards_G4()
    {
        var vm = CreateViewModel();

        vm.UpdateOtherAgents(Report(daily: ["copilot"]), null);

        Assert.All(vm.Cards, c => Assert.Equal("Checking...", c.StateWord));
    }
}
