using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// Decision C25 — test gaps found by P4 Inspector B (mutants M3, M7, M14 and finding F4), written
/// from SPEC R-014 §P4, §P3 (decisions C8, C9, C10, C19, C20) and R-004 only, per
/// Docs/AI_WORKFLOW.md §5.6. These tests must not add seams or reflection: where a mutant turns
/// out to be unkillable through the public API, the handoff asks for a report instead
/// (C25 (b)/(c)) — see the tests' comments and the handoff report.
/// </summary>
public sealed class ProviderTabAgentUsageGapTests
{
    // Value oracle "…(ccusage)" rows: "<TokensText> · <CostText>" (P4); C11 oracle texts.
    private const string RowGroup = "ccusage";
    private const string TodayLabel = "Today (ccusage)";
    private const string MonthLabel = "This month (ccusage)";
    private const string TodayValue = "1,234 · $1.23";
    private const string MonthValue = "9,999 · $10.50";

    private static AgentUsageLine TodayLine() => new("claude", "Claude Code", 1_234, "1,234", "$1.23");

    private static AgentUsageLine MonthLine() => new("claude", "Claude Code", 9_999, "9,999", "$10.50");

    private static AgentUsageSummary BothLines() => new(TodayLine(), MonthLine());

    /// <summary>The snapshot shape whose pre-R-014 observable output is pinned by
    /// <c>SupplementaryAgentUsageTests</c>/<c>ProviderTabViewModelTests</c>: rows
    /// Plan/Credits/Limit reason, tiles Lifetime tokens, "Rate Limit Resets (Codex)"/"Not verified".</summary>
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

    private static ProviderSnapshot UnconfiguredSnapshot(string providerId) =>
        new(providerId, configured: false);

    private static ProviderSnapshot ClaudeConfiguredLegacyStatsSnapshot() => new(
        "claude",
        configured: true,
        stats: [new Stat("Tokens Today", "15,200"), new Stat("Cost", "$0.45")]);

    /// <summary>The state the F4/R-004 regression checks compare: everything the R-014 rows must
    /// never disturb, as one readable string (failure diffs print both).</summary>
    private static string CaptureSection(ProviderTabViewModel vm) => string.Join(" | ",
        $"show={vm.ShowSupplementary}",
        $"section={vm.Supplementary is not null}",
        $"rows=[{string.Join(",", vm.SupplementaryRows.Select(r => $"{r.Label}={r.Value}({r.Group})"))}]",
        $"tiles=[{string.Join(",", vm.StatTiles.Select(t => $"{t.Label}={t.Value}"))}]",
        $"pills=[{string.Join(",", vm.ActivityPills.Select(p => $"{p.Label}={p.Value}"))}]",
        $"reset={(vm.Supplementary is null ? "(none)" : $"({vm.ResetTitle}|{vm.ResetBadge}|{vm.ResetSummary}|{vm.ResetDetails})")}",
        $"activityTitle={(vm.Supplementary is null ? "(none)" : vm.ActivityTitle)}",
        $"meters=[{string.Join(",", vm.Meters.Select(m => $"{m.Title}={m.ValueText}"))}]");

    /// <summary>The R-004 part of <see cref="CaptureSection"/>: gauges and stat tiles — the
    /// elements the ccusage rows must never touch, independent of the section's own visibility.</summary>
    private static string CaptureChrome(ProviderTabViewModel vm) => string.Join(" | ",
        $"tiles=[{string.Join(",", vm.StatTiles.Select(t => $"{t.Label}={t.Value}"))}]",
        $"meters=[{string.Join(",", vm.Meters.Select(m => $"{m.Title}={m.ValueText}"))}]");

    private static string TilesOf(ProviderTabViewModel vm) =>
        string.Join(",", vm.StatTiles.Select(t => $"{t.Label}={t.Value}"));

    // ---------------------------------------------------------------------------
    // C25(a) — mutant M3: provider ids outside the C9 map must not get ccusage rows
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("gemini")]
    [InlineData("legacy-provider")]
    public void SetAgentUsage_WhenProviderIdIsOutsideTheC9Map_DoesNotCreateOrChangeAnySection(string providerId)
    {
        // Decision C25(a), P4 Inspector B mutant M3. C9: the tab→agent map is exactly
        // { claude, openai, agy } — "other ids → no rows". The legacy Gemini id is the example
        // the decision names; its pre-R-014 look is pinned by
        // <c>SupplementaryDetailsTests.Gemini_NeverShowsSupplementarySectionOrFakeContent</c>.
        var vm = new ProviderTabViewModel(providerId, providerId);
        vm.UpdateFromSnapshot(new ProviderSnapshot(providerId, configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats: [new Stat("Requests today", "128")]));
        Assert.False(vm.ShowSupplementary); // existing rule: unmapped providers never show a section
        Assert.Null(vm.Supplementary);
        var before = CaptureSection(vm);

        // A summary WITH lines is the dangerous input: the C9 id check is what keeps it out.
        vm.SetAgentUsage(BothLines());

        Assert.Equal(before, CaptureSection(vm)); // Supplementary and ShowSupplementary unchanged
        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Group == RowGroup); // "no rows" (C9)
        Assert.Equal("Requests today", Assert.Single(vm.StatTiles).Label); // nothing leaked into tiles either (C8)
    }

    [Fact]
    public void SetAgentUsage_WhenProviderIdIsOutsideTheC9Map_AndNoSnapshot_StaysHiddenWithNoSection()
    {
        // C25(a)/C9, the "before any snapshot" arm: nothing exists to append to, and the id is
        // unmapped, so the section must stay absent (mutant M3 would create a ccusage-only
        // section here, since the claude/openai/agy branches never match).
        var vm = new ProviderTabViewModel("gemini", "Gemini");
        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);

        vm.SetAgentUsage(BothLines());

        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
        Assert.False(vm.HasResetInfo);
    }

    // ---------------------------------------------------------------------------
    // C25(b) — mutant M7: the !HasResetInfo clause of IsAgentUsageOnly
    // ---------------------------------------------------------------------------

    [Fact]
    public void SetAgentUsage_WhenCcUsageOnlySectionLaterMeetsConfiguredSnapshot_ResetsToFullSectionWithResetInfo()
    {
        // Decision C25(b), P4 Inspector B mutant M7 (dropping the !HasResetInfo clause of
        // IsAgentUsageOnly). This pins the spec behaviour around the clause from both sides:
        // a ccusage-only section has no reset part (C20/C19) and must BECOME a normal section
        // (reset card present, existing rows plus appended ccusage rows) the moment the existing
        // rule starts showing — P4 "UpdateFromSnapshot keeps using the stored summary", C20
        // "when the existing rule shows the section, the rows are appended as now", C10.
        // ResetTitle/ResetBadge oracle "Rate Limit Resets (Codex)"/"Not verified" is pinned by
        // <c>SupplementaryDetailsTests.Codex_WhenResetAbsentOrMalformed_…</c> for the snapshot
        // used below.
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        vm.UpdateFromSnapshot(UnconfiguredSnapshot("openai"));
        vm.SetAgentUsage(BothLines());

        // Side 1 of the clause: ccusage-only — HasResetInfo false, so IsAgentUsageOnly is true.
        Assert.True(vm.ShowSupplementary);
        Assert.Equal(2, vm.SupplementaryRows.Count);
        Assert.False(vm.HasResetInfo); // C19
        Assert.Equal("", vm.Supplementary!.ResetTitle); // C20: no default "Rate Limit Resets" title

        // Side 2: the same stored summary under a showing existing rule — the section is NOT
        // ccusage-only any more (reset info present and non-ccusage rows exist), and the stored
        // rows survive at the end (P4/C10).
        vm.UpdateFromSnapshot(OpenAiConfiguredSnapshot());

        Assert.True(vm.ShowSupplementary);
        Assert.True(vm.HasResetInfo); // C19: the reset card is back
        Assert.Equal("Rate Limit Resets (Codex)", vm.ResetTitle);
        Assert.Equal("Not verified", vm.ResetBadge);
        var rows = vm.SupplementaryRows;
        Assert.Equal(5, rows.Count); // 3 existing + 2 ccusage, appended last (C10/C20)
        Assert.Equal(["Plan", "Credits", "Limit reason"], rows.Take(3).Select(r => r.Label).ToArray());
        Assert.Equal([TodayLabel, MonthLabel], rows.Skip(3).Select(r => r.Label).ToArray());
        Assert.Equal([TodayValue, MonthValue], rows.Skip(3).Select(r => r.Value).ToArray());
        Assert.All(rows.Take(3), r => Assert.NotEqual(RowGroup, r.Group));
    }

    // ---------------------------------------------------------------------------
    // C25(c) — mutant M14: the StatTiles filter when _baseShowSupplementary != ShowSupplementary
    // ---------------------------------------------------------------------------

    [Fact]
    public void Claude_WhenCcUsageOnlySectionExists_GaugesAndStatTilesStayExactlyAsTheSnapshotMadeThem()
    {
        // Decision C25(c), P4 Inspector B mutant M14. C20: "a Claude tab with a ccusage-only
        // section still shows its stat tiles"; P4: "Gauges, charts, stat tiles unchanged (R-004)".
        // The flip state here is exactly the one the filter sees: the existing rule hides the
        // section (unconfigured → SupplementaryRegressionTests) while ShowSupplementary is true.
        // Note for the report: on Claude/OpenAI a `_baseShowSupplementary → ShowSupplementary`
        // substitution is provably equivalent — _base=false forces the snapshot to contain no
        // IsSupplementaryStat entries, so the filter keeps every stat under both spellings; the
        // discriminating arm is therefore the AGY test below. This test still pins the C20 rule
        // itself for the tab the decision names ("a Claude tab … still shows its stat tiles").
        var control = new ProviderTabViewModel("claude", "Claude");
        control.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: false,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats: [new Stat("Tokens Today", "15,200"), new Stat("Cost", "$0.45")]));
        Assert.False(control.ShowSupplementary);

        var vm = new ProviderTabViewModel("claude", "Claude");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: false,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats: [new Stat("Tokens Today", "15,200"), new Stat("Cost", "$0.45")]));

        vm.SetAgentUsage(BothLines());

        Assert.True(vm.ShowSupplementary); // ccusage-only (C20)
        var rows = vm.SupplementaryRows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(RowGroup, r.Group));

        // R-004/C20: the chrome (stat tiles, gauges) is byte-for-byte what the snapshot alone
        // produced — the filter may never hide or re-populate it because the section flipped on.
        Assert.Equal(CaptureChrome(control), CaptureChrome(vm));
        Assert.DoesNotContain(vm.StatTiles, t => t.Label.Contains("(ccusage)", StringComparison.Ordinal));

        // …and after removing the summary: the existing (hiding) visibility rule is back, the
        // rows are gone and the chrome is untouched — exactly the restore the suite already
        // pins for this state in <c>SetAgentUsage_WhenNull_AndExistingRuleHidesSection_…</c>
        // (C10). Whether the hidden placeholder section object itself survives is not an
        // observable the spec fixes (R-004 speaks of shown elements) — see report.
        vm.SetAgentUsage(null);
        Assert.False(vm.ShowSupplementary); // the existing rule alone hides it again (C10)
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Group == RowGroup); // rows removed (C10)
        Assert.Equal(CaptureChrome(control), CaptureChrome(vm)); // R-004 after the restore too
    }

    [Fact]
    public void Agy_WhenCcUsageOnlySectionExists_KeepsEverySnapshotStatInTheTiles()
    {
        // C25(c)/M14 kill site. The tiles filter in UpdateFromSnapshot reads
        // `!_baseShowSupplementary || !IsSupplementaryStat(s)` and runs AFTER ApplyAgentUsage()
        // has re-applied the stored summary — so a refresh while a summary with lines is stored
        // is exactly the state where _baseShowSupplementary (false for AGY, which is never the
        // append branch and whose existing rule hides the section) and ShowSupplementary (true
        // from the ccusage lines) diverge. The filter must not react to that divergence: C20
        // "still shows its stat tiles", R-004 "stat tiles unchanged", and "Plan" is pinned tile
        // content for a non-append tab by the pre-R-014 test
        // <c>OtherProviderStats_AreNotFiltered</c>. Mutants "filter on ShowSupplementary" and
        // "drop the !_baseShowSupplementary guard" move "Plan" out of the tiles on the SECOND
        // UpdateFromSnapshot below — SetAgentUsage alone never recomputes tiles, and no other
        // test observes the tiles at that point (the Inspector B gap), so they die here only.
        var snapshot = new ProviderSnapshot("agy", configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats: [new Stat("Plan", "Plus", "Reported tier"), new Stat("Requests today", "128")]);
        var vm = new ProviderTabViewModel("agy", "Gemini / AGY");

        vm.UpdateFromSnapshot(snapshot);
        Assert.False(vm.ShowSupplementary); // AGY existing rule (C8)
        Assert.Equal("Plan=Plus,Requests today=128", TilesOf(vm)); // pre-R-014: all stats stay tiles

        vm.SetAgentUsage(BothLines());
        Assert.True(vm.ShowSupplementary); // the ccusage-only flip
        Assert.Equal(2, vm.SupplementaryRows.Count);
        Assert.All(vm.SupplementaryRows, r => Assert.Equal(RowGroup, r.Group));
        Assert.Equal("Plan=Plus,Requests today=128", TilesOf(vm)); // tiles untouched by SetAgentUsage

        // The refresh that evaluates the filter while the divergence holds (ApplyAgentUsage
        // inside UpdateFromSnapshot runs before the tiles are computed):
        vm.UpdateFromSnapshot(snapshot);
        Assert.True(vm.ShowSupplementary); // the stored summary is still applied (P4)
        Assert.Equal("Plan=Plus,Requests today=128", TilesOf(vm)); // …without touching the tiles (C20/R-004)
        Assert.DoesNotContain(vm.StatTiles, t => t.Label.Contains("(ccusage)", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------
    // C25(d) — F4: the three live tabs must look exactly like before R-014 without any summary
    // ---------------------------------------------------------------------------

    public static TheoryData<string, bool> LiveTabs => new()
    {
        { "claude", true },
        { "openai", true },
        { "agy", false },
    };

    private static ProviderSnapshot LiveTabSnapshot(string providerId) => providerId switch
    {
        "openai" => OpenAiConfiguredSnapshot(),
        "claude" => ClaudeConfiguredLegacyStatsSnapshot(),
        _ => new ProviderSnapshot(providerId, configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats: [new Stat("Requests today", "128")]),
    };

    [Theory]
    [MemberData(nameof(LiveTabs))]
    public void SetAgentUsage_WhenNoSummaryOrNullSummary_TheTabIsIdenticalToTheNoR014CallSequence(string providerId, bool baselineShown)
    {
        // Decision C25(d), P4 Inspector B finding F4 (+ C10 "SetAgentUsage(null) removes the rows
        // and restores the existing visibility rule"; R-004 "no existing element changes").
        // A tab whose MainWindowViewModel passes SummaryFor = null must equal, element for
        // element, a tab SetAgentUsage was never called on — and so must a tab that briefly had
        // rows and lost them again.
        var baseline = new ProviderTabViewModel(providerId, providerId);
        baseline.UpdateFromSnapshot(LiveTabSnapshot(providerId));
        Assert.Equal(baselineShown, baseline.ShowSupplementary);

        var nullOnly = new ProviderTabViewModel(providerId, providerId);
        nullOnly.UpdateFromSnapshot(LiveTabSnapshot(providerId));
        nullOnly.SetAgentUsage(null);
        Assert.Equal(CaptureSection(baseline), CaptureSection(nullOnly));

        var roundTrip = new ProviderTabViewModel(providerId, providerId);
        roundTrip.UpdateFromSnapshot(LiveTabSnapshot(providerId));
        roundTrip.SetAgentUsage(BothLines()); // R-014 rows…
        roundTrip.SetAgentUsage(null); // …removed again — back to pre-R-014 exactly
        Assert.Equal(CaptureSection(baseline), CaptureSection(roundTrip));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("openai")]
    [InlineData("agy")]
    public void SetAgentUsage_WhenNull_AndNoSnapshotEver_StaysHiddenWithNoSection(string providerId)
    {
        // C25(d)/F4, the "before the first snapshot" arm: with no report (MainWindowViewModel
        // passes null until the first Updated event) the three live tabs keep the pre-R-014
        // empty look — the same state <c>ProviderTabViewModelTests.Constructor_InitializesProperties</c>
        // describes, and null must not create a section (unlike a summary WITH lines on claude/openai, C20).
        var vm = new ProviderTabViewModel(providerId, providerId);
        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);

        vm.SetAgentUsage(null);

        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
        Assert.False(vm.HasResetInfo); // forwarded false for "no section at all"
    }

    [Fact]
    public void AllLiveTabs_WithoutAnySummary_MatchThePinnedPreR014Oracles()
    {
        // C25(d)/F4 with absolute oracles (not control comparisons), every value pinned by the
        // pre-R-014 tests this mirrors: <c>UpdateFromSnapshot_WhenClaude_ResetAlwaysUnknownAndNoDuplicates</c>,
        // <c>UpdateFromSnapshot_WhenGemini_NoSupplementarySection</c>, <c>SupplementaryAgentUsageTests</c>
        // (openai base rows) and the AGY "stays as today" rule (C8). R-004: SetAgentUsage(null)
        // is a no-op against all of it.
        var claude = new ProviderTabViewModel("claude", "Claude");
        claude.UpdateFromSnapshot(ClaudeConfiguredLegacyStatsSnapshot());
        claude.SetAgentUsage(null);
        Assert.True(claude.ShowSupplementary);
        Assert.Equal("Not verified", claude.ResetBadge); // C19-era claude card, unchanged by R-014
        Assert.Equal("Tokens Today=15,200,Cost=$0.45",
            string.Join(",", claude.StatTiles.Select(t => $"{t.Label}={t.Value}")));
        Assert.DoesNotContain(claude.SupplementaryRows, r => r.Group == RowGroup);

        var openAi = new ProviderTabViewModel("openai", "OpenAI / Codex");
        openAi.UpdateFromSnapshot(OpenAiConfiguredSnapshot());
        openAi.SetAgentUsage(null);
        Assert.Equal(["Plan", "Credits", "Limit reason"], openAi.SupplementaryRows.Select(r => r.Label).ToArray());
        Assert.DoesNotContain(openAi.SupplementaryRows, r => r.Group == RowGroup);
        Assert.Equal("Lifetime tokens=4.5M",
            string.Join(",", openAi.StatTiles.Select(t => $"{t.Label}={t.Value}")));

        var agy = new ProviderTabViewModel("agy", "Gemini / AGY");
        agy.UpdateFromSnapshot(LiveTabSnapshot("agy"));
        agy.SetAgentUsage(null);
        Assert.False(agy.ShowSupplementary); // AGY "stays as today" without a summary (C8)
        Assert.Null(agy.Supplementary);
        Assert.Equal("Requests today=128",
            string.Join(",", agy.StatTiles.Select(t => $"{t.Label}={t.Value}")));
    }
}
