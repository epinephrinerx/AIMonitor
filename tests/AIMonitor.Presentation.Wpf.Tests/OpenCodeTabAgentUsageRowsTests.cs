using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-015 Q2.2 rows / decision E3 — <c>ProviderTabViewModel</c> treats <c>"opencode"</c> exactly like
/// <c>"agy"</c> for ccusage rows (C8/C9/C10/C19/C21 of SPEC_R014): SupportsAgentUsage =
/// AppendsAgentUsage || id is "agy" or "opencode" (case-insensitive); other ids (e.g. "gemini") still
/// ignore a summary (C25a). Written from Docs/SPEC_R015.md only (§5.6). Q2 tests: expected not to
/// compile against today's build until the Q2 branch merges (handoff note, E16).
/// </summary>
public sealed class OpenCodeTabAgentUsageRowsTests
{
    // C8/AGY-analogue oracles: ccusage-only section, group "ccusage", value "<TokensText> · <CostText>".
    private const string TodayLabel = "Today (ccusage)";
    private const string MonthLabel = "This month (ccusage)";
    private const string RowGroup = "ccusage";

    private static AgentUsageLine TodayLine() => new("opencode", "OpenCode", 1_234, "1,234", "$1.23");

    private static AgentUsageLine MonthLine() => new("opencode", "OpenCode", 9_999, "9,999", "$10.50");

    private static AgentUsageSummary BothLines() => new(TodayLine(), MonthLine());

    [Fact]
    public void Opencode_WhenSummaryHasLines_CreatesCcusageOnlySection_AndNullRestores()
    {
        // Q2.2 "treats \"opencode\" exactly like \"agy\"": a ccusage-only section with "Today
        // (ccusage)" / "This month (ccusage)", no reset part (C19-analogue); C10-analogue: removing
        // the summary hides it again because opencode has no base section of its own.
        var vm = new ProviderTabViewModel("opencode", "OpenCode");
        Assert.False(vm.ShowSupplementary); // no base rule shows the section for opencode (C8-analogue)

        vm.SetAgentUsage(BothLines());

        Assert.True(vm.ShowSupplementary);
        Assert.NotNull(vm.Supplementary);
        var rows = vm.SupplementaryRows;
        Assert.Equal(2, rows.Count); // only the ccusage rows
        Assert.Equal(TodayLabel, rows[0].Label);
        Assert.Equal(MonthLabel, rows[1].Label);
        Assert.Equal("1,234 · $1.23", rows[0].Value);
        Assert.Equal("9,999 · $10.50", rows[1].Value);
        Assert.All(rows, r => Assert.Equal(RowGroup, r.Group));
        Assert.False(vm.HasResetInfo); // no reset part
        Assert.Equal("", vm.Supplementary!.ResetTitle);
        Assert.Empty(vm.ActivityPills); // no activity part

        vm.UpdateFromSnapshot(new ProviderSnapshot("opencode", configured: true,
            meters: [new Meter("month_budget", "month_budget", "Month to date", "", 50.0)]));
        Assert.Equal(2, vm.SupplementaryRows.Count); // C10-analogue: rows survive the gauge update

        vm.SetAgentUsage(null);
        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
    }

    [Theory]
    // E3: the id check is case-insensitive.
    [InlineData("opencode")]
    [InlineData("OpenCode")]
    public void Opencode_E3_SupportsAgentUsageIsCaseInsensitive(string providerId)
    {
        var vm = new ProviderTabViewModel(providerId, "OpenCode");

        vm.SetAgentUsage(BothLines());

        var ccusageRows = vm.SupplementaryRows.Where(r => r.Group == RowGroup).ToList();
        Assert.Equal(2, ccusageRows.Count);
        Assert.True(vm.ShowSupplementary);
    }

    [Fact]
    public void Gemini_E3_OtherIds_StillIgnoreTheSummary()
    {
        // E3: "Other ids (e.g. gemini) still ignore a summary (C25a)."
        var vm = new ProviderTabViewModel("gemini", "Gemini");

        vm.SetAgentUsage(BothLines());

        Assert.Null(vm.Supplementary);
        Assert.False(vm.ShowSupplementary);
        Assert.Empty(vm.SupplementaryRows);
    }

    [Fact]
    public void Opencode_WhenSummaryHasNoLines_StaysHidden()
    {
        // C8-analogue: the section exists only while a ccusage line is present.
        var vm = new ProviderTabViewModel("opencode", "OpenCode");

        vm.SetAgentUsage(new AgentUsageSummary(null, null));

        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
    }
}
