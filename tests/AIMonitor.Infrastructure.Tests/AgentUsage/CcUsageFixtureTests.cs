using AIMonitor.Domain;
using AIMonitor.Infrastructure.AgentUsage;

namespace AIMonitor.Infrastructure.Tests.AgentUsage;

/// <summary>
/// Contract tests over the golden, synthetic fixtures under <c>tests/Fixtures/CcUsage/</c>
/// (shape of the real ccusage 20.0.48 run described in SPEC R-014, with invented ids, model names
/// and numbers; no secrets, no real account data). They exercise the parser end to end on a
/// realistic payload instead of only the narrow inline cases: <c>report-typical.json</c> is an
/// all-valid report and <c>report-mixed-invalid.json</c> carries the row-rule violations, including
/// the post-trial-merge decisions E1 (a model with no cost), E3 (an agents[] entry with no id, an
/// agent with no modelBreakdowns array, and a valid row whose agents array is empty) and E5 (a
/// non-string and a missing period).
/// </summary>
[Trait("Category", "Contract")]
public sealed class CcUsageFixtureTests
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CcUsage", name));

    [Fact]
    public void Parse_TypicalFixture_SortsCarriesOtherAndMissingPricingWithZeroSkips()
    {
        // Pricing/RetrievedAt pass-through (SPEC P1.3 Parse signature); Offline chosen to prove the
        // flag travels unchanged.
        var report = CcUsageJsonParser.Parse(Fixture("report-typical.json"), AgentUsagePricing.Offline, RetrievedAt);

        Assert.Equal(AgentUsagePricing.Offline, report.Pricing);
        Assert.Equal(RetrievedAt, report.RetrievedAt);
        Assert.Equal(0, report.SkippedRows);

        // SPEC P1.1: "Daily sorted by date ascending" — the fixture lists 11-07, 11-05, 11-06.
        Assert.Equal(
            new[] { new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 7) },
            report.Daily.Select(d => d.Date));

        // 2026-11-05 claude: Other = 4000 − (1000 + 2000 + 300 + 400 = 3700) = 300 (fixture data).
        // Row-level agent:"all" totals (888888) are ignored (SPEC P1.3), so exactly claude + codex.
        var day05 = report.Daily.Single(d => d.Date == new DateOnly(2026, 11, 5));
        Assert.Equal(2, day05.Agents.Count);
        var claude = day05.Agents.Single(a => a.Agent == "claude");
        Assert.Equal(300, claude.Tokens.Other);
        Assert.Equal(2, claude.Models.Count);
        Assert.Equal(0.0550m, claude.PricedCostUsd); // hand calc: 0.0425 + 0.0125 = 0.0550
        Assert.False(claude.HasUnpricedModels);
        // Model name kept exactly as printed (SPEC P1.1): "Claude-Opus-4-8".
        Assert.Contains(claude.Models, m => m.Model == "Claude-Opus-4-8");
        // 2026-11-05 codex: Other = 300 − (100 + 200 + 0 + 0 = 300) = 0.
        Assert.Equal(0, day05.Agents.Single(a => a.Agent == "codex").Tokens.Other);

        // 2026-11-06 antigravity: id " antigravity " trimmed+lowered (SPEC P1.1);
        // Other = 1300 − (500 + 500 + 100 + 100 = 1200) = 100;
        // missingPricing model → CostUsd null, PricedCostUsd = 0.00m (SPEC P1.4 oracle rule).
        var day06 = report.Daily.Single(d => d.Date == new DateOnly(2026, 11, 6));
        var antigravity = Assert.Single(day06.Agents);
        Assert.Equal("antigravity", antigravity.Agent);
        Assert.Equal(100, antigravity.Tokens.Other);
        Assert.True(antigravity.HasUnpricedModels);
        Assert.Equal(0.00m, antigravity.PricedCostUsd);
        Assert.Null(Assert.Single(antigravity.Models).CostUsd);

        // 2026-11-07 opencode: Other = 40 − (10 + 20 + 0 + 0 = 30) = 10.
        var opencode = Assert.Single(report.Daily.Single(d => d.Date == new DateOnly(2026, 11, 7)).Agents);
        Assert.Equal(10, opencode.Tokens.Other);

        // SPEC P1.1: "Monthly by (year, month) ascending" — the fixture lists 2026-11 then 2026-10.
        Assert.Equal(new[] { (2026, 10), (2026, 11) }, report.Monthly.Select(m => (m.Year, m.Month)));
        // 2026-10 claude: Other = 8000 − (2000 + 4000 + 600 + 800 = 7400) = 600.
        Assert.Equal(600, Assert.Single(report.Monthly.Single(m => m.Month == 10).Agents).Tokens.Other);
        // 2026-11 claude: Other = 4000 − 3700 = 300.
        Assert.Equal(300, Assert.Single(report.Monthly.Single(m => m.Month == 11).Agents).Tokens.Other);

        // SPEC P1.1: "Sessions by LastActivity descending". Fixture instants:
        // s-ccc 10:30Z > s-aaa 09:00Z > s-bbb 12:00+07:00 = 05:00Z (12:00 − 7 h = 05:00Z).
        Assert.Equal(new[] { "s-ccc", "s-aaa", "s-bbb" }, report.Sessions.Select(s => s.SessionId));
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 5, 0, 0, TimeSpan.Zero),
            report.Sessions.Single(s => s.SessionId == "s-bbb").LastActivity);
        // s-aaa agent "Claude" normalised (SPEC P1.1); Other = 900 − (300 + 400 + 50 + 50 = 800) = 100.
        var sessionA = report.Sessions.Single(s => s.SessionId == "s-aaa");
        Assert.Equal("claude", sessionA.Usage.Agent);
        Assert.Equal(100, sessionA.Usage.Tokens.Other);
        // s-bbb (row itself is the AgentUsage): Other = 50 − (20 + 30 + 0 + 0 = 50) = 0.
        Assert.Equal(0, report.Sessions.Single(s => s.SessionId == "s-bbb").Usage.Tokens.Other);
        // s-ccc: Other = 400 − (100 + 200 + 10 + 10 = 320) = 80; haiku model missingPricing → unpriced.
        var sessionC = report.Sessions.Single(s => s.SessionId == "s-ccc");
        Assert.Equal(80, sessionC.Usage.Tokens.Other);
        Assert.True(sessionC.Usage.HasUnpricedModels);
        Assert.Equal(0.00m, sessionC.Usage.PricedCostUsd);

        // Decision A3 + SPEC P1.1: read from root.totals.unpricedModels only; distinct, ordinal-sorted.
        // Input ["gemini-3", "claude-haiku-4-5", "gemini-3"] → the duplicate collapses; ordinal: 'c' < 'g'.
        Assert.Equal(new[] { "claude-haiku-4-5", "gemini-3" }, report.UnpricedModels);
    }

    [Fact]
    public void Parse_MixedInvalidFixture_CountsElevenSkippedRowsAndKeepsEveryValidRow()
    {
        // The fixture contains 15 rows: 11 invalid by spec rules and 4 valid.
        // Daily (9 rows, 7 invalid):
        //   "2026-02-30"            — fake calendar date (A4)
        //   "2026-11-05"            — VALID
        //   "2026-11-06"            — no agents array
        //   "2026-11-07"            — outputTokens is the string "20"
        //   20261108                — period is a number, not a string (E5)
        //   "2026-11-09"            — agents[] entry with no agent id (E3)
        //   "2026-11-10"            — agent object without a modelBreakdowns array (E3)
        //   "2026-11-11"            — model entry with cost absent and no missingPricing (E1)
        //   "2026-11-12"            — VALID: "agents": [] is a valid row with zero agents (E3)
        // Monthly (3 rows, 2 invalid): "2026-13" (A4), "2026-11" VALID, and a row with no period (E5).
        // Session (3 rows, 2 invalid): "s-ok" VALID, "s-no-metadata" (A5), "" blank session id.
        // Because at least one valid row exists, the all-rows-skipped rejection does not fire.
        var report = CcUsageJsonParser.Parse(
            Fixture("report-mixed-invalid.json"), AgentUsagePricing.OnlineRequested, RetrievedAt);

        Assert.Equal(11, report.SkippedRows);

        // SPEC P1.1: "Daily sorted by date ascending" — the two kept rows.
        Assert.Equal(
            new[] { new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 12) },
            report.Daily.Select(d => d.Date));

        var day = report.Daily.Single(d => d.Date == new DateOnly(2026, 11, 5));
        var claude = Assert.Single(day.Agents);
        // Other = 40 − (10 + 20 + 0 + 0 = 30) = 10 (fixture data).
        Assert.Equal(10, claude.Tokens.Other);
        Assert.Equal(0.001m, claude.PricedCostUsd);

        // Decision E3: "agents: [] → valid row with zero agents" — kept, and not counted as skipped.
        Assert.Empty(report.Daily.Single(d => d.Date == new DateOnly(2026, 11, 12)).Agents);

        var month = Assert.Single(report.Monthly);
        Assert.Equal((2026, 11), (month.Year, month.Month));

        var session = Assert.Single(report.Sessions);
        Assert.Equal("s-ok", session.SessionId);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero), session.LastActivity);

        // Decision A3: totals missing → no unpriced models, and no error.
        Assert.Empty(report.UnpricedModels);
    }
}
