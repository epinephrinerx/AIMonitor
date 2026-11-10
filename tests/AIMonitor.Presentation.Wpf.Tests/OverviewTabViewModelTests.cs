using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-016 H1.2 — <see cref="ProviderTabViewModel"/> behaviour for provider id "overview", written
/// from Docs/SPEC_R016.md only (§5.6): I3 (nullable <c>EmptyMessage</c>, set only for the Overview
/// tab: "Checking ccusage..." with no snapshot yet (state 1), "No agent usage recorded this month"
/// in state 3 with zero meters (O8), null in state 2 where only the error banner shows; other tabs
/// keep it null), I4 (rollover: a snapshot retrieved in an earlier month with no current-month
/// entry shows "Waiting for this month's ccusage data" instead; LastUpdated keeps
/// lastGood.RetrievedAt — the F11 style), I5 (<c>MeterColumns = min(count, 3)</c> for Overview —
/// e.g. 6 agents → 3 columns, wrapping to 2 rows — other tabs keep their existing value), E24
/// extended to "overview" (no setup card), H1.2's no-ccusage-section rule
/// (<c>SupportsAgentUsage</c> stays false) and H1.2's no-chart rule.
/// I3's "plus a visibility flag" is not named in the spec, so these tests assert <c>EmptyMessage</c>
/// itself: the flag is its non-nullness (XAML collapses the text element when null). Snapshots are
/// hand-built ProviderSnapshots, so nothing here leans on the builder (covered by
/// OverviewSnapshotBuilderTests). I8: each test uses one captured local day; the retrieval
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
    public void Ctor_I3_Overview_KeepsTheCheckingMessageWhileState1()
    {
        // I3: "With no snapshot yet (state 1) it is 'Checking ccusage...'". The builder returned null,
        // no snapshot was pushed, yet the tab must not look stuck — this text is what the user sees.
        var tab = OverviewTab();

        Assert.Equal(Checking, tab.EmptyMessage);
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
        Assert.Null(new ProviderTabViewModel(providerId, providerId).EmptyMessage);
    }

    [Fact]
    public void Update_I3_State2Error_ClearsTheEmptyMessageAndShowsOnlyTheBanner()
    {
        // I3: "In state 2 it is null and only the error banner shows." E24 extended to "overview":
        // an error-only snapshot never falls back to the setup card (configuredNow is false because
        // Ok requires Error == null — without the exemption the card would appear).
        var tab = OverviewTab();
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage was not found on PATH.");

        tab.UpdateFromSnapshot(
            OverviewSnapshot(configured: true, error: AgentUsagePresenter.ErrorText(failure.Kind, failure.Message)),
            LiveNow);

        Assert.Null(tab.EmptyMessage);
        Assert.Equal(AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage was not found on PATH."), tab.ErrorMessage);
        Assert.True(tab.HasError);
        Assert.False(tab.ShowSetupCard); // E24 exemption covers "overview" (H1.2)
        Assert.Empty(tab.MeterSections);
        Assert.Equal(0, tab.MeterColumns);
    }

    [Fact]
    public void Update_I3_O8_ZeroMetersThisMonth_ShowsTheRecordedMessage()
    {
        // I3 / O8: state 3 with zero meters → "No agent usage recorded this month". The retrieval is
        // from THIS month, so I4's rollover text does not apply.
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: []), LiveNow);

        Assert.Equal(Recorded, tab.EmptyMessage);
        Assert.Empty(tab.MeterSections); // no gauge cards
        Assert.True(tab.HasSnapshot); // the tab visibly changed — it does not look stuck
    }

    [Fact]
    public void Update_I4_StaleRetrievalWithNoCurrentMonthEntry_ShowsTheWaitingMessage()
    {
        // I4 rollover: the month of lastGood.RetrievedAt (= the snapshot's FetchedAt, state 3) is
        // last month and the current month has no entry (zero meters) → "Waiting for this month's
        // ccusage data" instead of I3's text.
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: [], fetchedAt: PreviousMonth), LiveNow);

        Assert.Equal(Waiting, tab.EmptyMessage);
    }

    [Fact]
    public void Update_I4_StaleRetrievalWithMeters_KeepsTheGaugesAndHasNoEmptyMessage()
    {
        // I4: "a failed refresh keeps the old gauges; that stays" — a stale retrieval with meters is
        // NOT the empty state: no empty message, and LastUpdated shows the retrieval instant
        // (F11 style), proving the tab is not stuck at "now".
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: [AgentCard("claude", 25.0, "$12.50 of $50.00 budget")], fetchedAt: PreviousMonth), LiveNow);

        Assert.Null(tab.EmptyMessage);
        Assert.Single(tab.Meters);
        Assert.Equal(PreviousMonth, tab.LastUpdated);
    }

    [Fact]
    public void Update_I3_MetersPresent_ClearsTheEmptyMessage()
    {
        // I3: the message exists only while there is nothing to show; with meters it is null.
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters:
        [
            AgentCard("claude", 25.0, "$12.50 of $50.00 budget"),
            AgentCard("codex", null, "$3.20 + unpriced · No budget set — add one in Settings"),
        ]), LiveNow);

        Assert.Null(tab.EmptyMessage);
        Assert.Equal(2, tab.MeterColumns); // I5: min(2, 3) = 2
        // Existing lead-meter tab caption applies unchanged (O2 percent hand-derived: 12.50 ÷ 50 × 100 = 25):
        Assert.Equal("Overview 25%", tab.TabTitle);
        Assert.Equal("--", tab.Meters[1].ValueText); // null percent renders dashes (existing card rule)
    }

    [Fact]
    public void Update_I5_SixAgents_WrapToThreeColumns()
    {
        // I5: "at most 3 gauge columns, wrapping to more rows" — the handoff's worked example:
        // 6 agents → min(6, 3) = 3 columns, which with 6 meters makes 6 ÷ 3 = 2 rows in the template.
        var tab = OverviewTab();

        tab.UpdateFromSnapshot(OverviewSnapshot(meters: Cards(6)), LiveNow);

        Assert.Equal(6, tab.Meters.Count);
        Assert.Equal(3, tab.MeterColumns);
        Assert.Null(tab.EmptyMessage);
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
        Assert.Equal(6, tab.MeterColumns); // NOT capped
        Assert.Null(tab.EmptyMessage); // I3: other tabs stay null even with meters present…
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
