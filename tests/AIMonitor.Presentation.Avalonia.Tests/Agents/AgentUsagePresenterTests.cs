using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.Agents;

/// <summary>
/// Tests for <see cref="AgentUsagePresenter"/> written from SPEC R-014 §P3.1 and the
/// Coding Owner decisions after P3/P4 Inspector A (C6, C11) and the P3 review (C18).
/// Oracles are quoted verbatim from C11.
/// </summary>
public sealed class AgentUsagePresenterTests
{
    private static AgentUsage Agent(string id, long total, params decimal?[] costs)
    {
        var models = new List<ModelUsage>();
        for (var i = 0; i < costs.Length; i++)
        {
            models.Add(new ModelUsage($"model-{i}", new TokenCounts(1, 0, 0, 0, 1), costs[i]));
        }

        return new AgentUsage(id, new TokenCounts(total, 0, 0, 0, total), models);
    }

    // ── AgentDisplayName (spec P3.1) ─────────────────────────────────────────

    [Theory]
    [InlineData("claude", "Claude Code")]
    [InlineData("codex", "Codex")]
    [InlineData("antigravity", "Antigravity")]
    [InlineData("opencode", "OpenCode")]
    [InlineData("copilot", "Copilot")]
    [InlineData("zcode", "ZCode")]
    [InlineData("geminicli", "geminicli")] // unknown ids pass through unchanged (P3.1)
    [InlineData("weird-agent", "weird-agent")]
    public void AgentDisplayName_MapsKnownIdsAndPassesUnknownThrough(string agent, string expected)
    {
        // Spec P3.1.
        Assert.Equal(expected, AgentUsagePresenter.AgentDisplayName(agent));
    }

    // ── CostText — C11 oracles by hand ───────────────────────────────────────

    [Fact]
    public void CostText_NoModels_ReturnsZeroDollars()
    {
        // Decision C11 oracle: [] → "$0.00" (spec P3.1: no models → "$0.00").
        var usage = Agent("claude", 100);

        Assert.Equal("$0.00", AgentUsagePresenter.CostText(usage));
    }

    [Fact]
    public void CostText_SingleUnpricedModel_ReturnsUnknownPrice()
    {
        // Decision C11 oracle: [null] → "Unknown price" (spec P3.1: every model unpriced).
        var usage = Agent("claude", 100, (decimal?)null);

        Assert.Equal("Unknown price", AgentUsagePresenter.CostText(usage));
    }

    [Fact]
    public void CostText_ZeroPricedPlusUnpriced_ShowsZeroThenUnpricedSuffix()
    {
        // Decision C11 oracle: [0.00] + [null] → "$0.00 + unpriced".
        // R-014 item 3: an unpriced model never contributes $0 — the suffix must still appear.
        var usage = Agent("claude", 100, 0.00m, null);

        Assert.Equal("$0.00 + unpriced", AgentUsagePresenter.CostText(usage));
    }

    [Fact]
    public void CostText_AllPriced_ReturnsMoney()
    {
        // Decision C11 oracle: [1.23] → "$1.23".
        var usage = Agent("claude", 100, 1.23m);

        Assert.Equal("$1.23", AgentUsagePresenter.CostText(usage));
    }

    [Fact]
    public void CostText_MixedPricedAndUnpriced_SumsPricedAndAddsSuffix()
    {
        // Decision C11 oracle: [1.00, 0.23, null] → "$1.23 + unpriced".
        var usage = Agent("claude", 100, 1.00m, 0.23m, null);

        Assert.Equal("$1.23 + unpriced", AgentUsagePresenter.CostText(usage));
    }

    [Fact]
    public void CostText_BelowOneCent_UsesMoneyFloorText()
    {
        // Decision C11 oracle (money rule): ChartFormat.Money(0.001) → "<$0.01".
        var usage = Agent("claude", 100, 0.001m);

        Assert.Equal("<$0.01", AgentUsagePresenter.CostText(usage));
    }

    // ── TokensText — C11 oracles ─────────────────────────────────────────────

    [Theory]
    [InlineData(9_999L, "9,999")]   // Decision C11 oracle
    [InlineData(10_000L, "10K")]    // Decision C11 oracle
    [InlineData(12_900L, "12.9K")]  // Decision C11 oracle
    public void TokensText_FormatsViaChartFormatCompact_OraclesFromC11(long tokens, string expected)
    {
        // Spec P3.1: TokensText = ChartFormat.Compact; values are the C11 oracle.
        Assert.Equal(expected, AgentUsagePresenter.TokensText(tokens));
    }

    // ── BuildDay / BuildMonth — C11 oracle + P3.1 rules ──────────────────────

    [Fact]
    public void BuildDay_C11Oracle_OrdersAgentsAggregatesTokensAndCost()
    {
        // Decision C11 oracle: claude (cost 1.23, total 1,000), codex (0.50, 2,000),
        // opencode (only unpriced, 500) → order claude, codex, opencode;
        // tokens 3,500 → "3,500"; period cost "$1.73 + unpriced".
        // Input is deliberately scrambled to prove the sort (spec P3.1).
        var claude = Agent("claude", 1_000, 1.23m);
        var codex = Agent("codex", 2_000, 0.50m);
        var opencode = Agent("opencode", 500, (decimal?)null);
        var day = new DailyAgentUsage(new DateOnly(2026, 11, 9), [opencode, codex, claude]);

        var period = AgentUsagePresenter.BuildDay(day);

        Assert.Equal("2026-11-09", period.Title); // P3.1: yyyy-MM-dd invariant
        Assert.Equal(3_500, period.Tokens);       // Σ agent Tokens.Total
        Assert.Equal("3,500", period.TokensText);
        Assert.Equal("$1.73 + unpriced", period.CostText);
        Assert.Equal(["claude", "codex", "opencode"], period.Agents.Select(a => a.Agent));
        Assert.Equal(["Claude Code", "Codex", "OpenCode"], period.Agents.Select(a => a.DisplayName));

        var claudeLine = period.Agents[0];
        Assert.Equal(1_000, claudeLine.Tokens);
        Assert.Equal("1,000", claudeLine.TokensText);
        Assert.Equal("$1.23", claudeLine.CostText);

        Assert.Equal("2,000", period.Agents[1].TokensText);
        Assert.Equal("$0.50", period.Agents[1].CostText);

        Assert.Equal("500", period.Agents[2].TokensText);
        Assert.Equal("Unknown price", period.Agents[2].CostText); // unpriced-only agent (C11/P3.1)
    }

    [Fact]
    public void BuildDay_TieBreaks_ThenTokensDesc_ThenIdOrdinal()
    {
        // Spec P3.1: sorted by PricedCostUsd desc, then tokens desc, then id ordinal.
        var beta = Agent("beta", 900, 0.50m);
        var alpha = Agent("alpha", 500, 0.50m);
        var zeta = Agent("zeta", 500, 0.50m);
        var day = new DailyAgentUsage(new DateOnly(2026, 11, 9), [zeta, beta, alpha]);

        var period = AgentUsagePresenter.BuildDay(day);

        // Equal cost 0.50: beta has more tokens; alpha before zeta by ordinal id.
        Assert.Equal(["beta", "alpha", "zeta"], period.Agents.Select(a => a.Agent));
    }

    [Fact]
    public void BuildDay_NoAgents_ZeroTokensAndZeroCost()
    {
        // Spec P3.1: no models → "$0.00"; period tokens = Σ = 0.
        var period = AgentUsagePresenter.BuildDay(new DailyAgentUsage(new DateOnly(2026, 11, 9), []));

        Assert.Equal("2026-11-09", period.Title);
        Assert.Equal(0, period.Tokens);
        Assert.Equal("0", period.TokensText);
        Assert.Equal("$0.00", period.CostText);
        Assert.Empty(period.Agents);
    }

    [Fact]
    public void BuildDay_AllAgentsUnpriced_PeriodCostIsUnknownPrice()
    {
        // Spec P3.1 + C11: every model unpriced → "Unknown price" for the period total too.
        var day = new DailyAgentUsage(new DateOnly(2026, 11, 9), [Agent("qwen", 500, (decimal?)null)]);

        var period = AgentUsagePresenter.BuildDay(day);

        Assert.Equal("Unknown price", period.CostText);
    }

    [Fact]
    public void BuildMonth_TitleIsInvariantMonthYearAndAggregates()
    {
        // Spec P3.1: month title "MMMM yyyy" (invariant, e.g. "November 2026").
        var month = new MonthlyAgentUsage(2026, 11, [Agent("claude", 1_000, 1.23m)]);

        var period = AgentUsagePresenter.BuildMonth(month);

        Assert.Equal("November 2026", period.Title);
        Assert.Equal(1_000, period.Tokens);
        Assert.Equal("1,000", period.TokensText);
        Assert.Equal("$1.23", period.CostText);
    }

    // ── PricingText / ErrorText ──────────────────────────────────────────────

    [Fact]
    public void PricingText_OnlineRequested_ReturnsC6ShortText()
    {
        // Decision C6 overrides the P3.1 wording: "Prices: online".
        Assert.Equal("Prices: online", AgentUsagePresenter.PricingText(AgentUsagePricing.OnlineRequested));
    }

    [Fact]
    public void PricingText_Offline_ReturnsBuiltInText()
    {
        // Decision C6: "Prices: offline (built-in)".
        Assert.Equal("Prices: offline (built-in)", AgentUsagePresenter.PricingText(AgentUsagePricing.Offline));
    }

    [Theory]
    [InlineData(AgentUsageErrorKind.NotInstalled, "ccusage was not found. Install it with `npm i -g ccusage`, then restart AIMonitor.")]
    [InlineData(AgentUsageErrorKind.Timeout, "ccusage did not finish in time.")]
    [InlineData(AgentUsageErrorKind.OutputTooLarge, "ccusage output was too large.")]
    [InlineData(AgentUsageErrorKind.Failed, "ccusage could not read usage. Run `ccusage daily` in a terminal to see why.")]
    [InlineData(AgentUsageErrorKind.InvalidResponse, "ccusage returned an unexpected response.")]
    public void ErrorText_EveryKind_ReturnsTheMessageVerbatim(AgentUsageErrorKind kind, string message)
    {
        // Spec P3.1: NotInstalled → the exception message (it already says how to install);
        // others → message. The message is never rewritten.
        Assert.Equal(message, AgentUsagePresenter.ErrorText(kind, message));
    }

    // ── SummaryFor — C11 oracle + P3.1 rules ─────────────────────────────────

    private static readonly DateOnly Today = new(2026, 11, 9);

    private static AgentUsageReport SummaryReport()
    {
        var claudeToday = Agent("claude", 1_000, 1.23m);
        var onlyToday = Agent("opencode", 700, 0.20m);
        var claudeMonth = Agent("claude", 5_000, 6.00m);
        var codexMonth = Agent("codex", 3_000, (decimal?)null);
        var zcodeEarlier = Agent("zcode", 200, 0.30m);

        return new AgentUsageReport(
            [
                new DailyAgentUsage(Today, [claudeToday, onlyToday]),
            ],
            [
                new MonthlyAgentUsage(2026, 10, [zcodeEarlier]),
                new MonthlyAgentUsage(2026, 11, [claudeMonth, codexMonth]),
            ],
            [],
            [],
            AgentUsagePricing.OnlineRequested,
            new DateTimeOffset(2026, 11, 9, 7, 3, 22, TimeSpan.Zero),
            0);
    }

    [Fact]
    public void SummaryFor_NullReport_ReturnsNull()
    {
        // Spec P3.1: null when the report is null.
        Assert.Null(AgentUsagePresenter.SummaryFor(null, "claude", Today));
    }

    [Fact]
    public void SummaryFor_AgentInTodayRow_UsesTodayRowForTodayAndMonthRowForThisMonth()
    {
        // Spec P3.1: Today from today's daily row, ThisMonth from the current month row.
        var summary = AgentUsagePresenter.SummaryFor(SummaryReport(), "claude", Today);

        Assert.NotNull(summary);
        Assert.Equal(1_000, summary!.Today!.Tokens);
        Assert.Equal("1,000", summary.Today.TokensText);
        Assert.Equal("$1.23", summary.Today.CostText);
        Assert.Equal("Claude Code", summary.Today.DisplayName);
        Assert.Equal("claude", summary.Today.Agent);
        Assert.Equal(5_000, summary.ThisMonth!.Tokens);
        Assert.Equal("$6.00", summary.ThisMonth.CostText);
    }

    [Fact]
    public void SummaryFor_AgentOnlyInThisMonthRow_TodayIsNullThisMonthIsSet()
    {
        // Decision C11 oracle: agent in this month's row but not today's → Today null, ThisMonth set.
        var summary = AgentUsagePresenter.SummaryFor(SummaryReport(), "codex", Today);

        Assert.NotNull(summary);
        Assert.Null(summary!.Today);
        Assert.NotNull(summary.ThisMonth);
        Assert.Equal("Codex", summary.ThisMonth!.DisplayName);
        Assert.Equal(3_000, summary.ThisMonth.Tokens);
        Assert.Equal("3,000", summary.ThisMonth.TokensText);
        Assert.Equal("Unknown price", summary.ThisMonth.CostText); // only unpriced model (C11 rule)
    }

    [Fact]
    public void SummaryFor_AgentOnlyInEarlierMonth_ReturnsNull()
    {
        // Decision C11 oracle: agent only in an earlier month → null.
        Assert.Null(AgentUsagePresenter.SummaryFor(SummaryReport(), "zcode", Today));
    }

    [Fact]
    public void SummaryFor_AgentNotInReportAtAll_ReturnsNull()
    {
        // Spec P3.1: null when the agent appears in neither today's daily row nor this month's row.
        Assert.Null(AgentUsagePresenter.SummaryFor(SummaryReport(), "copilot", Today));
    }

    [Fact]
    public void SummaryFor_AgentOnlyInTodayRow_ThisMonthIsNull()
    {
        // Spec P3.1: null only when the agent is in neither row; one line may be null.
        var summary = AgentUsagePresenter.SummaryFor(SummaryReport(), "opencode", Today);

        Assert.NotNull(summary);
        Assert.NotNull(summary!.Today);
        Assert.Null(summary.ThisMonth);
    }
}
