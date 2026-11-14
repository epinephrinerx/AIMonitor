using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// P4 — ccusage rows in the existing tabs (SPEC R-014 "P4" section; decisions C8, C10, C19, C20, C21).
/// Written from the spec only (Docs/AI_WORKFLOW.md §5.6): the P4 API is the one announced in the spec and
/// handoff — <see cref="ProviderTabViewModel.SetAgentUsage"/> plus the P3 records
/// <c>AgentUsageSummary(AgentUsageLine? Today, AgentUsageLine? ThisMonth)</c> and
/// <c>AgentUsageLine(string Agent, string DisplayName, long Tokens, string TokensText, string CostText)</c>.
/// These tests do not compile until the P3/P4 code branches merge (expected per §5.8 and the handoff).
/// </summary>
public sealed class SupplementaryAgentUsageTests
{
    // P4: row labels are fixed strings; the group is "ccusage"; the value is "<TokensText> · <CostText>".
    // Oracle strings follow the C11 oracles (TokensText "9,999"; CostText "$1.23").
    private const string TodayLabel = "Today (ccusage)";
    private const string MonthLabel = "This month (ccusage)";
    private const string RowGroup = "ccusage";
    private const string TodayValue = "1,234 · $1.23";
    private const string MonthValue = "9,999 · $10.50";

    private static AgentUsageLine TodayLine() => new("claude", "Claude Code", 1_234, "1,234", "$1.23");

    private static AgentUsageLine MonthLine() => new("claude", "Claude Code", 9_999, "9,999", "$10.50");

    private static AgentUsageSummary BothLines() => new(TodayLine(), MonthLine());

    /// <summary>Claude/OpenAI rule today: configured (or supplementary stats present) shows the section.</summary>
    private static ProviderSnapshot OpenAiConfiguredSnapshot() => new(
        "openai",
        configured: true,
        meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
        stats:
        [
            new Stat("Plan", "Plus", "ChatGPT plan"),
            new Stat("Credits", "150", "Workspace credits"),
            new Stat("Limit reason", "Rate limit reached", "Reported by Codex"),
            new Stat("Lifetime tokens", "4.5M", "Reported by Codex"),
        ]);

    // ---------------------------------------------------------------------------
    // P4 core: row labels / values / group / order; rows never in StatTiles
    // ---------------------------------------------------------------------------

    [Fact]
    public void SetAgentUsage_WhenBothLinesPresent_AppendsCcusageRowsLastTodayThenMonth_StatTilesUnchanged()
    {
        // P4: group "ccusage", appended after the existing rows, Today before This month,
        // value "<TokensText> · <CostText>"; "Gauges, charts, stat tiles unchanged (R-004)".
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());

        var baseRows = vm.SupplementaryRows.Select(r => r.Label).ToList();
        Assert.Equal(["Plan", "Credits", "Limit reason"], baseRows);
        var tilesBefore = vm.StatTiles.Select(t => (t.Label, t.Value)).ToList();

        vm.SetAgentUsage(BothLines());

        Assert.True(vm.ShowSupplementary);
        var rows = vm.SupplementaryRows;
        Assert.Equal(baseRows.Count + 2, rows.Count);
        Assert.Equal(baseRows, rows.Take(baseRows.Count).Select(r => r.Label));
        Assert.Equal(TodayLabel, rows[^2].Label);
        Assert.Equal(MonthLabel, rows[^1].Label);
        Assert.Equal(TodayValue, rows[^2].Value);
        Assert.Equal(MonthValue, rows[^1].Value);
        Assert.Equal(RowGroup, rows[^2].Group);
        Assert.Equal(RowGroup, rows[^1].Group);

        // C8/P4: rows go to Supplementary.Rows only, never to the stat tiles.
        Assert.Equal(tilesBefore, vm.StatTiles.Select(t => (t.Label, t.Value)).ToList());
        Assert.DoesNotContain(vm.StatTiles, t => t.Label.Contains("(ccusage)", StringComparison.Ordinal));
    }

    [Fact]
    public void SetAgentUsage_WhenOnlyTodayLinePresent_OmitsThisMonthRow()
    {
        // P4: "a missing line is omitted".
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());
        var baseCount = vm.SupplementaryRows.Count;

        vm.SetAgentUsage(new AgentUsageSummary(TodayLine(), null));

        var row = Assert.Single(vm.SupplementaryRows, r => r.Group == RowGroup);
        Assert.Equal(TodayLabel, row.Label);
        Assert.Equal(TodayValue, row.Value);
        Assert.Equal(baseCount + 1, vm.SupplementaryRows.Count);
        Assert.Equal(TodayLabel, vm.SupplementaryRows[^1].Label);
    }

    [Fact]
    public void SetAgentUsage_WhenOnlyThisMonthLinePresent_OmitsTodayRow()
    {
        // P4: "a missing line is omitted" (SummaryFor gives Today null, ThisMonth set — C11).
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());
        var baseCount = vm.SupplementaryRows.Count;

        vm.SetAgentUsage(new AgentUsageSummary(null, MonthLine()));

        var row = Assert.Single(vm.SupplementaryRows, r => r.Group == RowGroup);
        Assert.Equal(MonthLabel, row.Label);
        Assert.Equal(MonthValue, row.Value);
        Assert.Equal(baseCount + 1, vm.SupplementaryRows.Count);
    }

    [Fact]
    public void SetAgentUsage_WhenSummaryHasNoLines_AddsNoRows_AndExistingVisibilityStands()
    {
        // P4: "ShowSupplementary = <existing rule> || summary has at least one line" — a summary with
        // two missing lines has none, so nothing is added and the existing rule keeps the section shown.
        var vm = new ProviderTabViewModel("claude", "Claude");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true));
        var baseCount = vm.SupplementaryRows.Count;

        vm.SetAgentUsage(new AgentUsageSummary(null, null));

        Assert.Equal(baseCount, vm.SupplementaryRows.Count);
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Group == RowGroup);
        Assert.True(vm.ShowSupplementary);
    }

    // ---------------------------------------------------------------------------
    // C10 — SetAgentUsage(null) restores the existing rows and visibility rule
    // ---------------------------------------------------------------------------

    [Fact]
    public void SetAgentUsage_WhenNull_AndExistingRuleShowsSection_RemovesCcusageRowsAndKeepsSection()
    {
        // C10: "SetAgentUsage(null) removes the rows and restores the existing visibility rule".
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());
        var baseCount = vm.SupplementaryRows.Count;

        vm.SetAgentUsage(BothLines());
        Assert.Equal(baseCount + 2, vm.SupplementaryRows.Count);

        vm.SetAgentUsage(null);

        Assert.Equal(baseCount, vm.SupplementaryRows.Count);
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Group == RowGroup);
        Assert.True(vm.ShowSupplementary); // the existing rule alone shows it again
    }

    [Fact]
    public void SetAgentUsage_WhenNull_AndExistingRuleHidesSection_HidesTheSectionAgain()
    {
        // C10 + C20: after the ccusage-only section is gone, the existing rule (unconfigured → hidden)
        // applies again.
        var vm = new ProviderTabViewModel("claude", "Claude");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: false));
        Assert.False(vm.ShowSupplementary);

        vm.SetAgentUsage(BothLines());
        Assert.True(vm.ShowSupplementary);

        vm.SetAgentUsage(null);
        Assert.False(vm.ShowSupplementary);
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Group == RowGroup);
    }

    [Fact]
    public void UpdateFromSnapshot_WhenSummaryStored_RowsSurviveAndStayAppendedLast()
    {
        // P4: "UpdateFromSnapshot keeps using the stored summary"; C10: "rows survive UpdateFromSnapshot".
        var vm = new ProviderTabViewModel("claude", "Claude");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 40.0)]));

        vm.SetAgentUsage(BothLines());
        var withRows = vm.SupplementaryRows.Count;
        Assert.True(withRows >= 2);

        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 55.0)],
            stats: [new Stat("Plan", "Team", "Reported tier")]));

        var rows = vm.SupplementaryRows;
        var ccusageRows = rows.Where(r => r.Group == RowGroup).ToList();
        Assert.Equal(2, ccusageRows.Count); // the stored summary is still applied (C10)
        Assert.Equal(TodayLabel, rows[^2].Label); // still appended last, Today then This month (P4)
        Assert.Equal(MonthLabel, rows[^1].Label);
        Assert.Equal(TodayValue, rows[^2].Value);
        Assert.Equal(MonthValue, rows[^1].Value);
        Assert.Contains(rows, r => r.Label == "Plan" && r.Value == "Team"); // rebuilt from the new snapshot
    }

    [Fact]
    public void SetAgentUsage_WhenResetDetailsExpanded_KeepsExpansionAcrossRebuilds()
    {
        // C10: "IsResetDetailsExpanded kept" — across the SetAgentUsage rebuild, a later snapshot
        // refresh, and the null restore.
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());
        vm.Supplementary!.IsResetDetailsExpanded = true;

        vm.SetAgentUsage(BothLines());
        Assert.True(vm.Supplementary!.IsResetDetailsExpanded);

        vm.UpdateFromSnapshot(new ProviderSnapshot("openai", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 42.0)]));
        Assert.True(vm.Supplementary!.IsResetDetailsExpanded);

        vm.SetAgentUsage(null);
        Assert.True(vm.Supplementary!.IsResetDetailsExpanded);
    }

    // ---------------------------------------------------------------------------
    // C8 — AGY: ccusage-only section when a summary has lines, unchanged otherwise
    // ---------------------------------------------------------------------------

    [Fact]
    public void Agy_WhenSummaryHasLines_CreatesCcusageOnlySection_AndStaysUnchangedWithoutSummary()
    {
        // C8: "when a ccusage summary has a line, Supplementary is created with only the ccusage rows
        // (no reset or activity parts) and ShowSupplementary = true; otherwise AGY stays as today".
        var vm = new ProviderTabViewModel("agy", "Gemini / AGY");
        var snapshot = new ProviderSnapshot("agy", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats: [new Stat("Requests today", "128")]);

        // Without a summary AGY stays as today: nothing shown (existing rule).
        vm.UpdateFromSnapshot(snapshot);
        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);

        vm.SetAgentUsage(BothLines());

        Assert.True(vm.ShowSupplementary);
        Assert.NotNull(vm.Supplementary);
        var rows = vm.SupplementaryRows;
        Assert.Equal(2, rows.Count); // only the ccusage rows (C8)
        Assert.Equal(TodayLabel, rows[0].Label);
        Assert.Equal(MonthLabel, rows[1].Label);
        Assert.Equal(TodayValue, rows[0].Value);
        Assert.Equal(MonthValue, rows[1].Value);
        Assert.All(rows, r => Assert.Equal(RowGroup, r.Group));
        Assert.False(vm.HasResetInfo); // C19: ccusage-only has no reset part
        Assert.Empty(vm.ActivityPills); // C8: no activity part
        Assert.False(vm.HasSupplementaryActivity);

        // C8: "Rows go to Supplementary.Rows only, never to snapshot.Stats or stat tiles" —
        // AGY keeps its own stat tiles as today.
        Assert.Equal("Requests today", Assert.Single(vm.StatTiles).Label);
        Assert.DoesNotContain(vm.StatTiles, t => t.Label.Contains("(ccusage)", StringComparison.Ordinal));

        // C10: the rows survive a snapshot refresh.
        vm.UpdateFromSnapshot(new ProviderSnapshot("agy", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 35.0)],
            stats: [new Stat("Requests today", "140")]));
        Assert.Equal(2, vm.SupplementaryRows.Count);
        Assert.True(vm.ShowSupplementary);

        // C8 + C10: removing the summary returns AGY to "as today".
        vm.SetAgentUsage(null);
        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
    }

    [Fact]
    public void Agy_WhenSummaryHasNoLines_StaysAsToday()
    {
        // C8: "when a ccusage summary has a line" — without lines AGY stays as today (hidden).
        var vm = new ProviderTabViewModel("agy", "Gemini / AGY");
        vm.UpdateFromSnapshot(new ProviderSnapshot("agy", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)]));

        vm.SetAgentUsage(new AgentUsageSummary(null, null));

        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
    }

    // ---------------------------------------------------------------------------
    // C20 — Claude/OpenAI become ccusage-only when the existing rule hides the section
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData("openai", "OpenAI / Codex")]
    public void ClaudeOrOpenAi_WhenUnconfigured_AndSummaryHasLines_CreatesCcusageOnlySection(string providerId, string displayName)
    {
        // C20: "when the existing rule would hide the section (no snapshot yet, or unconfigured), a
        // summary with a line creates a ccusage-only section exactly like AGY (no placeholder
        // Plan/Credits rows, no reset part, no default 'Rate Limit Resets' title)".
        var vm = new ProviderTabViewModel(providerId, displayName);
        vm.UpdateFromSnapshot(new ProviderSnapshot(providerId, configured: false));
        Assert.False(vm.ShowSupplementary); // the existing rule hides it (SupplementaryRegressionTests)

        vm.SetAgentUsage(BothLines());

        Assert.True(vm.ShowSupplementary);
        var rows = vm.SupplementaryRows;
        Assert.Equal(2, rows.Count); // ccusage rows only — no placeholder Plan/Credits rows (C20)
        Assert.DoesNotContain(rows, r => r.Label is "Plan" or "Credits");
        Assert.Equal(TodayLabel, rows[0].Label);
        Assert.Equal(MonthLabel, rows[1].Label);
        Assert.Equal(TodayValue, rows[0].Value);
        Assert.Equal(MonthValue, rows[1].Value);
        Assert.All(rows, r => Assert.Equal(RowGroup, r.Group));

        // C20/C19: no reset part and no default "Rate Limit Resets" title.
        Assert.Equal("", vm.Supplementary!.ResetTitle);
        Assert.Equal("", vm.Supplementary!.ResetBadge);
        Assert.Equal("", vm.Supplementary!.ResetSummary);
        Assert.Equal("", vm.Supplementary!.ResetDetails);
        Assert.False(vm.Supplementary!.HasResetInfo);
        Assert.False(vm.HasResetInfo); // C19: ProviderTabViewModel forwards HasResetInfo
        Assert.Empty(vm.ActivityPills);
    }

    [Fact]
    public void Claude_WhenNoSnapshotYet_AndSummaryHasLines_CreatesCcusageOnlySection()
    {
        // C20: the "before any snapshot" case — SetAgentUsage alone must show the ccusage-only section.
        var vm = new ProviderTabViewModel("claude", "Claude");
        Assert.False(vm.HasSnapshot);
        Assert.False(vm.ShowSupplementary);

        vm.SetAgentUsage(BothLines());

        Assert.True(vm.ShowSupplementary);
        var rows = vm.SupplementaryRows;
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Label is "Plan" or "Credits"); // C20: no placeholder rows
        Assert.Equal(TodayLabel, rows[0].Label);
        Assert.Equal(MonthLabel, rows[1].Label);
        Assert.Equal("", vm.Supplementary!.ResetTitle); // C20: no default "Rate Limit Resets" title
        Assert.False(vm.HasResetInfo); // C19
    }

    // ---------------------------------------------------------------------------
    // C19 — HasResetInfo: false for a ccusage-only section, true for the reset cards
    // ---------------------------------------------------------------------------

    [Fact]
    public void HasResetInfo_IsTrueForNormalClaudeAndOpenAiResetCards_AndForwardedByTab()
    {
        // C19: HasResetInfo = any of ResetTitle/ResetBadge/ResetSummary/ResetDetails non-empty.
        // The normal Claude card has "Limit Resets (Claude)" + "Not verified" → true; the Codex card
        // falls back to "Rate Limit Resets (Codex)" + "Not verified" → true.
        var claude = new ProviderTabViewModel("claude", "Claude");
        claude.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true));
        Assert.True(claude.Supplementary!.HasResetInfo);
        Assert.True(claude.HasResetInfo); // C19: forwarded with the other dependent properties

        var openAi = new ProviderTabViewModel("openai", "OpenAI / Codex");
        openAi.UpdateFromSnapshot(new ProviderSnapshot("openai", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 20.0)]));
        Assert.Equal("Not verified", openAi.ResetBadge);
        Assert.True(openAi.Supplementary!.HasResetInfo);
        Assert.True(openAi.HasResetInfo);

        // Appending rows to an existing (reset-bearing) section keeps the reset card and HasResetInfo
        // true, and keeps the existing rows (the ccusage rows are only appended — P4, C10).
        var openAiBaseCount = openAi.SupplementaryRows.Count;
        openAi.SetAgentUsage(BothLines());
        Assert.True(openAi.HasResetInfo);
        Assert.Equal(openAiBaseCount + 2, openAi.SupplementaryRows.Count);
        Assert.DoesNotContain(openAi.SupplementaryRows.Take(openAiBaseCount), r => r.Group == RowGroup);

        // No section at all → the forwarded value is false.
        Assert.False(new ProviderTabViewModel("gemini", "Gemini").HasResetInfo);
    }

    // ---------------------------------------------------------------------------
    // C21 — no churn when the ccusage rows do not change
    // ---------------------------------------------------------------------------

    [Fact]
    public void SetAgentUsage_WhenSameSummaryTwice_KeepsSameSupplementaryInstanceAndRaisesNoPropertyChanged()
    {
        // C21: "Skip rebuilding Supplementary when the computed ccusage rows equal the current ones
        // (no churn on every refresh)."
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());

        var summary = BothLines();
        vm.SetAgentUsage(summary);
        var section = vm.Supplementary!;
        Assert.NotNull(section);

        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        // An equal (by record value equality) summary…
        vm.SetAgentUsage(new AgentUsageSummary(TodayLine(), MonthLine()));
        // …and the identical instance must not rebuild or notify.
        vm.SetAgentUsage(summary);

        Assert.Same(section, vm.Supplementary);
        Assert.True(vm.ShowSupplementary);
        Assert.Equal(5, vm.SupplementaryRows.Count); // 3 existing + 2 ccusage, unchanged
        Assert.Empty(changes);
    }

    [Fact]
    public void SetAgentUsage_WhenRowsActuallyChange_RebuildsTheSection()
    {
        // C21 read the other way: different rows are not "equal the current ones" — the section
        // rebuilds (guards the C21 fast path from being "always skip").
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());
        vm.SetAgentUsage(BothLines());
        var section = vm.Supplementary!;

        vm.SetAgentUsage(new AgentUsageSummary(TodayLine(), null)); // This month row omitted (P4)

        Assert.NotSame(section, vm.Supplementary);
        var ccusageRows = vm.SupplementaryRows.Where(r => r.Group == RowGroup).ToList();
        Assert.Single(ccusageRows);
        Assert.Equal(TodayLabel, ccusageRows[0].Label);
    }
}
