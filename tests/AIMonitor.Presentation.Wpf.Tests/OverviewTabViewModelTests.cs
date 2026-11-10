using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-016 H1.2 — <see cref="ProviderTabViewModel"/> behaviour for provider id "overview", written
/// from Docs/SPEC_R016.md (§5.6), aligned in round 2 with decisions I12–I15: the message TEXTS and
/// the rollover condition live in <c>OverviewSnapshotBuilder.EmptyMessage</c> (I12 — tested in
/// OverviewSnapshotBuilderTests), and the view model only accepts the hand-off:
/// <c>UpdateOverviewEmptyMessage(string?)</c> sets <c>EmptyMessage</c> for the Overview tab and is
/// ignored by every other tab (I3 "plus a visibility flag" — the code names it
/// <c>HasEmptyMessage</c>; I13's non-overview exemption). I13: a directly constructed
/// <c>new ProviderTabViewModel("overview", "Overview")</c> already carries the I10 ASCII state-1
/// text "Checking ccusage..." and the I5 3-column cap. I14: the tab caption stays plain
/// "Overview", never "Overview NN%". I5: <c>MeterColumns = min(count, 3)</c> for Overview (the
/// cap's XAML consumer wraps rows; I11's gap itself is not pixel-tested, but the wrap signal
/// <c>MeterSection.HasMultipleRows</c> is asserted where the spec is silent on names — code names
/// win), other tabs keep their existing value. E24 extended to "overview" (no setup card), H1.2's
/// no-ccusage-section rule (<c>SupportsAgentUsage</c> stays false) and H1.2's no-chart rule.
/// Snapshots are hand-built ProviderSnapshots, so nothing here leans on the builder (covered by
/// OverviewSnapshotBuilderTests); <c>UpdateFromSnapshot</c> no longer computes any message (I12),
/// which these tests pin directly. I8: each test uses one captured instant; the retrieval
/// timestamps sit on day 15 of their month, 12:00 UTC, so no ±14 h local conversion can shift the
/// compared month — behaviour is independent of the machine's zone.
/// </summary>
public sealed class OverviewTabViewModelTests
{
    private static readonly DateTimeOffset LiveNow = DateTimeOffset.UtcNow; // one captured instant (I8)
    private static readonly DateOnly CapturedToday = DateOnly.FromDateTime(LiveNow.LocalDateTime);

    // Day-15-noon-UTC instants: their local month is the calendar month for every UTC±14 zone.
    private static readonly DateTimeOffset ThisMonth =
        new(CapturedToday.Year, CapturedToday.Month, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PreviousMonth = ThisMonth.AddMonths(-1);

    private const string Checking = "Checking ccusage...";
    private const string Recorded = "No agent usage recorded this month";
    private const string Waiting = "Waiting for this month's ccusage data";

    private static ProviderTabViewModel OverviewTab() =>
        new(OverviewSnapshotBuilder.ProviderId, "Overview");

    private static Meter AgentCard(string agent, double? percent, string subtitle) =>
        new(
            kind: "month_budget",
            key: "agent:" + agent,
            title: agent,
            subtitle: subtitle,
            percent: percent,
            resetsAt: new DateTimeOffset(CapturedToday.Year, CapturedToday.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1),
            detail: "$1.00");

    private static IReadOnlyList<Meter> Cards(int count) =>
        Enumerable.Range(1, count).Select(i => AgentCard($"agent{i}", percent: null, subtitle: "s" + i)).ToList();

    private static ProviderSnapshot OverviewSnapshot(
        IReadOnlyList<Meter>? meters = null,
        bool configured = true,
        string? error = null,
        DateTimeOffset? fetchedAt = null) =>
        new(
            providerId: OverviewSnapshotBuilder.ProviderId,
            configured: configured,
            meters: meters,
            error: error,
            fetchedAt: fetchedAt ?? ThisMonth);

    // ── I3: the empty-message mechanism ─────────────────────────────────────────

    [Fact]
    public void Ctor_I13_Overview_KeepsTheCheckingMessageWhileState1()
    {
        // I13: the initial text derives from ProviderId inside the constructor, so a directly
        // constructed tab — before any builder has ever run — shows I3's state-1 text ("it must not
        // look stuck") with I10's exact ASCII spelling ("Checking ccusage...", three dots, not
        // U+2026) and the I5 cap ready (0 meters so far → 0 columns).
        var tab = OverviewTab();

        Assert.Equal(Checking, tab.EmptyMessage);
        Assert.DoesNotContain('\u2026', tab.EmptyMessage!); // I10
        Assert.True(tab.HasEmptyMessage); // I3's "visibility flag" — code name, asserted where visible
        Assert.Equal("Checking...", tab.Status);
        Assert.Equal(0, tab.MeterColumns); // I5: min(count = 0, 3) = 0
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("openai")]
    [InlineData("agy")]
    [InlineData("opencode")]
    public void Ctor_I3_OtherTabs_KeepEmptyMessageNull(string providerId)
    {
        // I3: "Other tabs keep EmptyMessage null and look the same." (Set only for the Overview tab.)
        var tab = new ProviderTabViewModel(providerId, providerId);

        Assert.Null(tab.EmptyMessage);
        Assert.False(tab.HasEmptyMessage);
    }

    [Fact]
    public void Update_I3_State2Error_ShowsOnlyTheBannerAfterTheHandoff()
    {
        // I3 state 2: "null and only the error banner shows". Round-2 design (I12): the snapshot
        // update itself no longer touches the message — MainWindowViewModel hands off the builder's
        // state-2 value (null) via UpdateOverviewEmptyMessage. E24 extended to "overview": an
        // error-only snapshot never falls back to the setup card (configuredNow is false because
        // Ok requires Error == null — without the exemption the card would appear).
        var tab = OverviewTab();
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage was not found on PATH.");

        tab.UpdateFromSnapshot(
            OverviewSnapshot(configured: true, error: AgentUsagePresenter.ErrorText(failure.Kind, failure.Message)),
            LiveNow);
        tab.UpdateOverviewEmptyMessage(null); // I3 state 2 — the builder returns null (I12 tests pin that)

        Assert.Null(tab.EmptyMessage);
        Assert.Equal(AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage was not found on PATH."), tab.ErrorMessage);
        Assert.True(tab.HasError);
        Assert.False(tab.ShowSetupCard); // E24 exemption covers "overview" (H1.2)
        Assert.Empty(tab.MeterSections);
        Assert.Equal(0, tab.MeterColumns);
    }

    [Fact]
    public void UpdateOverviewEmptyMessage_I13_TheHandoffSetsAndClearsTheOverviewTab()
    {
        // I13: the builder's result reaches the tab through this method (I12), and UpdateFromSnapshot
        // itself no longer derives any text — the constructor's state-1 text survives the snapshot
        // until the hand-off runs. The three texts are asserted as carried values (their selection is
        // OverviewSnapshotBuilderTests' I12 job).
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: []), LiveNow);
        Assert.Equal(Checking, tab.EmptyMessage); // I12: a snapshot update does not compute the message

        tab.UpdateOverviewEmptyMessage(Recorded); // state 3 with zero meters (I3/O8)
        Assert.Equal(Recorded, tab.EmptyMessage);
        Assert.True(tab.HasEmptyMessage);

        tab.UpdateOverviewEmptyMessage(Waiting); // I4 rollover text
        Assert.Equal(Waiting, tab.EmptyMessage);

        tab.UpdateOverviewEmptyMessage(null); // state 2, or meters present
        Assert.Null(tab.EmptyMessage);
        Assert.False(tab.HasEmptyMessage);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("openai")]
    [InlineData("agy")]
    [InlineData("opencode")]
    public void UpdateOverviewEmptyMessage_I13_OtherTabsIgnoreTheHandoff(string providerId)
    {
        // I13: "the I12 hand-off goes through a method that ignores non-Overview tabs"; I3: "set
        // only for the Overview tab" — any value, even the state-1 text, leaves other tabs blank.
        var tab = new ProviderTabViewModel(providerId, providerId);

        tab.UpdateOverviewEmptyMessage(Checking);
        tab.UpdateOverviewEmptyMessage(Recorded);

        Assert.Null(tab.EmptyMessage);
        Assert.False(tab.HasEmptyMessage);
    }

    [Fact]
    public void Update_I3_O8_ZeroMetersThisMonth_CarriesTheRecordedMessage_AndDoesNotLookStuck()
    {
        // O8/I3 through the VM: state 3 with zero meters — the hand-off text is shown while the tab
        // is visibly updated (HasSnapshot true, no gauge cards), so "it must not look stuck".
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: []), LiveNow);
        tab.UpdateOverviewEmptyMessage(Recorded); // the builder's message for this state (I12)

        Assert.Equal(Recorded, tab.EmptyMessage);
        Assert.Empty(tab.MeterSections); // no gauge cards
        Assert.True(tab.HasSnapshot); // the tab visibly changed
    }

    [Fact]
    public void Update_I4_StaleRetrievalWithMeters_KeepsTheGaugesAndTheRetrievedInstant()
    {
        // I4: "a failed refresh keeps the old gauges; that stays" — LastUpdated shows the retrieval
        // instant (F11 style), proving the tab is not stuck at "now". With meters there is no empty
        // state to message, so the wiring hands off null (the text question is I12/I3's, pinned in
        // OverviewSnapshotBuilderTests and MainWindowViewModelOverviewTabTests).
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: [AgentCard("claude", 25.0, "$12.50 of $50.00 budget")], fetchedAt: PreviousMonth), LiveNow);
        tab.UpdateOverviewEmptyMessage(null);

        Assert.Null(tab.EmptyMessage);
        Assert.Single(tab.Meters);
        Assert.Equal(PreviousMonth, tab.LastUpdated);
    }

    [Fact]
    public void Update_I14_BudgetedMeters_KeepThePlainOverviewCaption()
    {
        // I14: "The Overview tab's caption is always plain 'Overview', never 'Overview NN%'" — even
        // though the lead meter (claude, O2 hand-derived 12.50 ÷ 50 × 100 = 25) would otherwise put
        // "25%" in the caption (the other-tab lead-meter rule is unchanged, see the claude guard in
        // Update_I5_OtherTabs_KeepTheirExistingColumnRule and the R-004 tab-caption tests).
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters:
        [
            AgentCard("claude", 25.0, "$12.50 of $50.00 budget"),
            AgentCard("codex", null, "$3.20 + unpriced · No budget set — add one in Settings"),
        ]), LiveNow);

        Assert.Equal("Overview", tab.TabTitle); // I14 — not "Overview 25%"
        Assert.Equal(2, tab.MeterColumns); // I5: min(2, 3) = 2 — the direct-construction cap (I13)
        Assert.Equal("--", tab.Meters[1].ValueText); // null percent renders dashes (existing card rule)
        Assert.False(Assert.Single(tab.MeterSections).HasMultipleRows); // 2 cards in 2 columns: no wrap (I11 signal)
    }

    [Fact]
    public void Update_I5_SixAgents_WrapToThreeColumns()
    {
        // I5: "at most 3 gauge columns, wrapping to more rows" — the handoff's worked example:
        // 6 agents → min(6, 3) = 3 columns. I11's pixel gap is not testable here, but the wrap
        // signal the XAML uses (code name MeterSection.HasMultipleRows — the spec is silent on
        // names, so the code's name wins) is pinned: 6 meters over 3 columns = more than one row.
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: Cards(6)), LiveNow);

        Assert.Equal(6, tab.Meters.Count);
        Assert.Equal(3, tab.MeterColumns);
        Assert.True(Assert.Single(tab.MeterSections).HasMultipleRows); // wraps to 2 rows (I11)
    }

    [Fact]
    public void Update_I5_OtherTabs_KeepTheirExistingColumnRule()
    {
        // I5: "Other tabs keep their current column rule unchanged." The current rule (MeterSection.
        // Columns) is the largest section row — 6 for six unsectioned Claude meters. The identical
        // 6-meter snapshot on a Claude-shaped tab must therefore report 6, not 3.
        var tab = new ProviderTabViewModel("claude", "Claude");
        var snapshot = new ProviderSnapshot(
            providerId: "claude",
            configured: true,
            meters: Cards(6),
            fetchedAt: ThisMonth);

        tab.UpdateFromSnapshot(snapshot, LiveNow);

        Assert.Equal(Assert.Single(tab.MeterSections).Columns, tab.MeterColumns); // the existing value
        Assert.Equal(6, tab.MeterColumns); // NOT capped (I5: the cap is Overview's only)
        Assert.False(Assert.Single(tab.MeterSections).HasMultipleRows); // I11: other tabs stay one-row
        Assert.Null(tab.EmptyMessage); // I3: other tabs stay null even with meters present…

        // The lead-meter caption rule (R-004) is unchanged on other tabs — the I14 override is
        // Overview-only. Cards(6) all have percent null, so re-apply a percented lead instead:
        var capped = new ProviderTabViewModel("claude", "Claude");
        capped.UpdateFromSnapshot(
            new ProviderSnapshot(
                providerId: "claude",
                configured: true,
                meters: [AgentCard("claude", 25.0, "$12.50 of $50.00 budget")],
                fetchedAt: ThisMonth),
            LiveNow);
        Assert.Equal("Claude 25%", capped.TabTitle); // "Overview NN%" ban (I14) does NOT leak here
    }

    [Fact]
    public void Update_I3_OtherTabs_ZeroMeters_StillHaveNoEmptyMessage()
    {
        // I3 tail: the zero-meter empty texts exist only for Overview — an OpenCode snapshot with no
        // meters keeps EmptyMessage null (its own "no empty gauge card" behaviour is unchanged).
        var tab = new ProviderTabViewModel(OpenCodeSnapshotBuilder.ProviderId, "OpenCode");

        tab.UpdateFromSnapshot(
            new ProviderSnapshot(providerId: OpenCodeSnapshotBuilder.ProviderId, configured: true, meters: [], fetchedAt: ThisMonth),
            LiveNow);

        Assert.Empty(tab.Meters);
        Assert.Null(tab.EmptyMessage);
    }

    // ── H1.2: setup-card exemption, no ccusage section, no chart ────────────────

    [Fact]
    public void Update_E24_NotConfiguredOverview_NeverShowsTheSetupCardLikeOpenCode()
    {
        // E24 extended (H1.2): "no setup card" — a configured:false, error-free, meter-free snapshot
        // (reachable if wiring ever pushes one) must not draw the setup card on Overview, while
        // Claude keeps R-004's card (regression guard for "look the same").
        var overview = OverviewTab();
        overview.UpdateFromSnapshot(OverviewSnapshot(meters: [], configured: false, error: null), LiveNow);

        Assert.False(overview.ShowSetupCard);
        Assert.Equal("", overview.AccountLabel);

        var claude = new ProviderTabViewModel("claude", "Claude");
        claude.UpdateFromSnapshot(
            new ProviderSnapshot(providerId: "claude", configured: false, meters: [], fetchedAt: ThisMonth),
            LiveNow);
        Assert.True(claude.ShowSetupCard);
    }

    [Fact]
    public void SetAgentUsage_H12_OverviewNeverShowsTheCcusageSection()
    {
        // H1.2: "no ccusage supplementary section (SupportsAgentUsage stays false for it)" — even a
        // populated summary produces no rows on the Overview tab, before or after a snapshot update.
        var tab = OverviewTab();
        var line = new AgentUsageLine("claude", "Claude", 1_234, "1,234", "$2.50");

        tab.SetAgentUsage(new AgentUsageSummary(line, line));

        Assert.False(tab.ShowSupplementary);
        Assert.Empty(tab.SupplementaryRows);

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: [AgentCard("claude", 25.0, "$12.50 of $50.00 budget")]), LiveNow);

        Assert.False(tab.ShowSupplementary);
        Assert.Empty(tab.SupplementaryRows);
    }

    [Fact]
    public void Update_H12_OverviewShowsNoChart()
    {
        // H1.2: "no chart" — the Overview snapshots carry history: null (H1.1 state 3), so the chart
        // cards stay hidden and there are no buckets.
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: Cards(2)), LiveNow);

        Assert.False(tab.HasHistory);
        Assert.Empty(tab.ChartBuckets);
    }
}
