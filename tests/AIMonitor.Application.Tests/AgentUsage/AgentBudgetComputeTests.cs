using System.Globalization;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;

namespace AIMonitor.Application.Tests.AgentUsage;

// The enclosing namespace AIMonitor.Application contains the segment "AgentUsage", which otherwise
// shadows the domain record of the same name (CS0118). An alias declared *after* the file-scoped
// namespace belongs to this namespace's declaration context and resolves the bare name.
using AgentUsage = AIMonitor.Domain.AgentUsage;

/// <summary>
/// R-015 Q1.2 — <see cref="AgentBudget.Compute"/> written from Docs/SPEC_R015.md only (§5.6):
/// oracles O1–O5 and decisions E7 (blank agent → ArgumentException("agent"), month row matched on
/// year AND month, reading.Agent normalised), E17 (a 0.00-class budget is never usable) and E21 (the
/// budget is found case-insensitively whatever comparer the caller's dictionary has), plus the Q1.2
/// rules (month from today.Year/Month, only report.Monthly is read, case-insensitive agent match,
/// budget &lt;= 0 absent, null report → null, blank agent → ArgumentException, null budgets →
/// ArgumentNullException, pure / never throws for a valid report). Percent oracle maths are
/// hand-derived in each test.
/// </summary>
public sealed class AgentBudgetComputeTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static AgentUsage Usage(string agent, params (string Model, decimal? Cost)[] models)
    {
        var tokenCounts = models.Select(m => new TokenCounts(100, 0, 0, 0, 100)).ToList();
        var usage = models
            .Zip(tokenCounts, (m, t) => new ModelUsage(m.Model, t, m.Cost))
            .ToList();
        return new AgentUsage(agent, new TokenCounts(0, 0, 0, 0, 100L * models.Length), usage);
    }

    private static AgentUsageReport Report(params MonthlyAgentUsage[] monthly) =>
        new(
            daily: [],
            monthly: monthly,
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: new DateTimeOffset(2026, 11, 10, 7, 3, 0, TimeSpan.Zero),
            skippedRows: 0);

    private static AgentUsageReport NovemberReport(params AgentUsage[] agents) =>
        Report(new MonthlyAgentUsage(2026, 11, agents));

    private static IReadOnlyDictionary<string, decimal> Budgets(params (string Agent, decimal Usd)[] entries) =>
        entries.ToDictionary(e => e.Agent, e => e.Usd);

    // ── O1–O5: the oracle table ────────────────────────────────────────────────

    [Fact]
    public void Compute_O1_PricedCostWithBudget_PercentIsCostOverBudget()
    {
        // O1: month priced cost 12.345, budget 50 → MonthCostUsd 12.345, Percent 24.69.
        // Hand-derived: 12.345m / 50m = 0.2469m; × 100 = 24.69 exactly in decimal, NOT capped.
        var report = NovemberReport(
            Usage("opencode", ("glm-4.6", 12.345m)),
            Usage("claude", ("claude-opus-5", 500m))); // other agents are not this reading

        var reading = AgentBudget.Compute(report, "opencode", Budgets(("opencode", 50m)), Today);

        Assert.NotNull(reading);
        Assert.Equal("opencode", reading.Agent);           // E7: normalised name
        Assert.Equal(12.345m, reading.MonthCostUsd);
        Assert.False(reading.HasUnpricedModels);
        Assert.Equal(50m, reading.BudgetUsd);
        Assert.Equal(24.69, reading.Percent!.Value);
        Assert.True(reading.HasUsageThisMonth);
    }

    [Fact]
    public void Compute_O2_PercentOverHundred_NotCapped()
    {
        // O2: cost 60, budget 50 → Percent 120 (not capped). 60/50 = 1.2 × 100 = 120.
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 60m)));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(("opencode", 50m)), Today);

        Assert.NotNull(reading);
        Assert.Equal(120.0, reading.Percent!.Value);
    }

    [Fact]
    public void Compute_O3_NoBudget_PercentAndBudgetUsdAreNull()
    {
        // O3: cost 7.5, no budget → Percent null (BudgetUsd null too); usage this month stands.
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 7.5m)));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(), Today);

        Assert.NotNull(reading);
        Assert.Equal(7.5m, reading.MonthCostUsd);
        Assert.Null(reading.BudgetUsd);
        Assert.Null(reading.Percent);
        Assert.True(reading.HasUsageThisMonth);
    }

    [Fact]
    public void Compute_O4_AgentOnlyLastMonth_ZeroCostButBudgetPercentZero()
    {
        // O4: only a last-month row → MonthCostUsd 0, HasUsageThisMonth false; budget 10 → Percent 0.
        // Hand-derived: 0m / 10m = 0 × 100 = 0.
        var report = Report(
            new MonthlyAgentUsage(2026, 10, [Usage("opencode", ("glm-4.6", 999m))]),
            new MonthlyAgentUsage(2026, 11, [Usage("claude", ("claude-opus-5", 1m))]));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(("opencode", 10m)), Today);

        Assert.NotNull(reading);
        Assert.Equal(0m, reading.MonthCostUsd);
        Assert.False(reading.HasUnpricedModels);
        Assert.False(reading.HasUsageThisMonth);
        Assert.Equal(10m, reading.BudgetUsd);
        Assert.Equal(0.0, reading.Percent!.Value);
    }

    [Fact]
    public void Compute_O5_UnpricedModel_NotCountedAsCostButSetsFlag()
    {
        // O5: model A cost 3, model B unpriced → MonthCostUsd 3 (priced only), HasUnpricedModels true.
        // (The "$3.00 + unpriced" gauge text is a Q2 presentation rule — see OpenCodeSnapshotBuilderTests.)
        var report = NovemberReport(Usage("opencode", ("model-a", 3m), ("model-b", null)));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(), Today);

        Assert.NotNull(reading);
        Assert.Equal(3m, reading.MonthCostUsd);
        Assert.True(reading.HasUnpricedModels);
    }

    // ── Q1.2: reading rules ────────────────────────────────────────────────────

    [Fact]
    public void Compute_Q12_OnlyMonthlyRowsAreRead_NotTheSumOfDailyRows()
    {
        // Q1.2: "Only report.Monthly is read (not the sum of daily rows)." Daily rows in the same
        // month would add up differently (100 + 100 + 100); the month row says 12.
        var month = new MonthlyAgentUsage(2026, 11, [Usage("opencode", ("glm-4.6", 12m))]);
        var report = new AgentUsageReport(
            daily:
            [
                new DailyAgentUsage(new DateOnly(2026, 11, 9), [Usage("opencode", ("glm-4.6", 100m))]),
                new DailyAgentUsage(new DateOnly(2026, 11, 10), [Usage("opencode", ("glm-4.6", 100m))]),
                new DailyAgentUsage(new DateOnly(2026, 11, 11), [Usage("opencode", ("glm-4.6", 100m))]),
            ],
            monthly: [month],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: new DateTimeOffset(2026, 11, 12, 0, 30, 0, TimeSpan.Zero),
            skippedRows: 0);

        var reading = AgentBudget.Compute(report, "opencode", Budgets(), Today);

        Assert.NotNull(reading);
        Assert.Equal(12m, reading.MonthCostUsd); // not 312, not 112
    }

    [Fact]
    public void Compute_E7_MonthMatchesYearAndMonthNotMonthAlone()
    {
        // E7: month row = m.Year == today.Year && m.Month == today.Month. A same-month/other-year row
        // comes FIRST in the list, so an implementation that matches on the month number alone reads
        // 5m from November 2025 instead of 2m from November 2026. December rows share the month number
        // with each other but match neither.
        var report = Report(
            new MonthlyAgentUsage(2025, 11, [Usage("opencode", ("glm-4.6", 5m))]),
            new MonthlyAgentUsage(2025, 12, [Usage("opencode", ("glm-4.6", 6m))]),
            new MonthlyAgentUsage(2026, 12, [Usage("opencode", ("glm-4.6", 7m))]),
            new MonthlyAgentUsage(2026, 11, [Usage("opencode", ("glm-4.6", 2m))]));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(("opencode", 8m)), Today);

        Assert.NotNull(reading);
        Assert.Equal(2m, reading.MonthCostUsd); // November 2026 only
        Assert.True(reading.HasUsageThisMonth);
        Assert.Equal(25.0, reading.Percent); // hand-derived: 2m / 8m = 0.25 × 100 = 25
    }

    [Fact]
    public void Compute_Q12_AgentAbsentFromMonthlyYetBudgeted_PercentZero()
    {
        // Q1.2: "Missing month or agent → MonthCostUsd = 0, HasUnpricedModels = false,
        // HasUsageThisMonth = false"; the budget still divides into 0 → Percent 0.
        var report = NovemberReport(Usage("codex", ("gpt-5-codex", 8m)));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(("opencode", 20m)), Today);

        Assert.NotNull(reading);
        Assert.Equal(0m, reading.MonthCostUsd);
        Assert.False(reading.HasUnpricedModels);
        Assert.False(reading.HasUsageThisMonth);
        Assert.Equal(0.0, reading.Percent!.Value);
    }

    [Fact]
    public void Compute_Q12_NoMonthRowForThisMonthAtAll_ReadsAsMissingMonth()
    {
        // Q1.2: "Missing month or agent → MonthCostUsd = 0, HasUnpricedModels = false,
        // HasUsageThisMonth = false" — here the report has NO row for November at all, only an older
        // month that carries the agent (and an unpriced model). Falling back to any other month row
        // (last/first) is what this pins down, not merely picking the wrong agent.
        var report = Report(
            new MonthlyAgentUsage(2026, 9, [Usage("opencode", ("model-a", 40m), ("model-b", null))]));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(("opencode", 10m)), Today);

        Assert.NotNull(reading);
        Assert.Equal(0m, reading.MonthCostUsd);      // the September row is not this month's cost
        Assert.False(reading.HasUnpricedModels);      // the flag belongs to the missing month, too
        Assert.False(reading.HasUsageThisMonth);
        Assert.Equal(10m, reading.BudgetUsd);
        Assert.Equal(0.0, reading.Percent);           // hand-derived: 0m / 10m = 0 × 100 = 0
    }

    [Fact]
    public void Compute_Q12_EmptyReport_ReadingWithZeroCost_NotThrowing()
    {
        // Q1.2: "Never throws for a valid report" — a report with no rows at all reads as zero.
        var reading = AgentBudget.Compute(Report(), "opencode", Budgets(), Today);

        Assert.NotNull(reading);
        Assert.Equal(0m, reading.MonthCostUsd);
        Assert.False(reading.HasUsageThisMonth);
        Assert.Null(reading.Percent);
    }

    [Fact]
    public void Compute_Q12_NullReport_ReturnsNull()
    {
        // Q1.2: report == null → returns null (nothing known yet).
        Assert.Null(AgentBudget.Compute(null, "opencode", Budgets(("opencode", 50m)), Today));
    }

    // ── Q1.2: case-insensitivity, budget-as-absent ─────────────────────────────

    [Fact]
    public void Compute_Q12_AgentMatchIsCaseInsensitiveAndBudgetLookupToo()
    {
        // Q1.2: "Agent match is case-insensitive on the normalised name" and
        // "Budget lookup by agent (case-insensitive)". The budget dict here uses the default comparer.
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 10m)));

        var reading = AgentBudget.Compute(report, "OpenCode", Budgets(("OPENCODE", 50m)), Today);

        Assert.NotNull(reading);
        Assert.Equal("opencode", reading.Agent); // E7: agent.Trim().ToLowerInvariant()
        Assert.Equal(10m, reading.MonthCostUsd);
        Assert.Equal(50m, reading.BudgetUsd);
    }

    [Theory]
    // E21: "AgentBudget.Compute finds the budget case-insensitively whatever comparer the caller's
    // dictionary has (exact TryGetValue first, then an OrdinalIgnoreCase scan over trimmed keys)".
    // Oracle: a default-comparer (ordinal) dictionary { "OpenCode": 10 } → BudgetUsd == 10. The exact
    // TryGetValue misses, so only the trimmed, case-insensitive scan can find it. Last row: a padded
    // key needs the Trim() of that scan; "co dex" must never match "opencode".
    [InlineData("OpenCode", 10.0)]      // E21 oracle: different case, ordinal dictionary
    [InlineData(" OPENCODE ", 10.0)]    // E21: padded key found by the trimmed scan
    [InlineData("opencode", 10.0)]      // exact hit on the first branch
    [InlineData("codex", null)]         // another agent's budget is not this reading's
    public void Compute_E21_BudgetIsFoundCaseInsensitively_WhateverComparerTheCallerUses(
        string budgetKey,
        double? expectedBudget)
    {
        // (decimal cannot be an attribute argument — the expected budget is written as a double?
        // only as an "absent / present" marker and compared against the exact 10m.)
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 5m)));
        var budgets = new Dictionary<string, decimal> { [budgetKey] = 10m }; // default = ordinal comparer

        var reading = AgentBudget.Compute(report, "opencode", budgets, Today);

        Assert.NotNull(reading);
        if (expectedBudget is null)
        {
            Assert.Null(reading.BudgetUsd);
            Assert.Null(reading.Percent);
        }
        else
        {
            Assert.Equal(10m, reading.BudgetUsd);
            Assert.Equal(50.0, reading.Percent); // hand-derived: 5m / 10m = 0.5 × 100 = 50
        }
    }

    [Fact]
    public void Compute_E21_OrderIgnoringDictionary_HitsTheExactTryGetValueBranch()
    {
        // E21: the exact TryGetValue branch still works for a caller that already uses the
        // OrdinalIgnoreCase comparer that Normalize produces.
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 8m)));
        var budgets = AppSettingsBudgetsLikeNormalize(("OpenCode", 16m));

        var reading = AgentBudget.Compute(report, "opencode", budgets, Today);

        Assert.NotNull(reading);
        Assert.Equal(16m, reading.BudgetUsd);
        Assert.Equal(50.0, reading.Percent); // hand-derived: 8m / 16m = 0.5 × 100 = 50
    }

    [Fact]
    public void Compute_Q12_E17_NonPositiveBudgetIsIgnoredByBothLookupBranches()
    {
        // Q1.2 "a budget <= 0 is treated as absent" — also on the E21 scan branch, where a padded
        // ordinal dictionary must not report 0.004 (or 0) as a budget.
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 5m)));

        var ordinal = AgentBudget.Compute(
            report, "opencode", new Dictionary<string, decimal> { ["OpenCode"] = 0m }, Today);
        var padded = AgentBudget.Compute(
            report, "opencode", new Dictionary<string, decimal> { [" OpenCode "] = 0m }, Today);

        Assert.NotNull(ordinal);
        Assert.Null(ordinal.BudgetUsd);
        Assert.Null(ordinal.Percent);
        Assert.NotNull(padded);
        Assert.Null(padded.BudgetUsd);
        Assert.Null(padded.Percent);
    }

    private static IReadOnlyDictionary<string, decimal> AppSettingsBudgetsLikeNormalize(
        params (string Agent, decimal Usd)[] entries)
    {
        // Normalize() hands the caller a fresh OrdinalIgnoreCase dictionary (E8) — same shape here.
        return entries.ToDictionary(
            e => e.Agent.Trim().ToLowerInvariant(),
            e => e.Usd,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_Q12_ReportRowNameCaseIsIgnoredToo_NotOnlyTheRequestedAgent()
    {
        // Q1.2: "Agent match is case-insensitive on the normalised name" — ccusage may name the row
        // "OpenCode" while the caller asks for "opencode"; the month row must still be the one read
        // (cost 10m, HasUsageThisMonth true), not treated as a missing agent. Note that AgentUsage
        // itself normalises its Agent (trim + lower-case), so the row arrives lower-cased here; this
        // test pins the pairing rather than the row's raw spelling.
        var report = NovemberReport(Usage("OpenCode", ("glm-4.6", 10m)));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(("opencode", 25m)), Today);

        Assert.NotNull(reading);
        Assert.Equal("opencode", reading.Agent);
        Assert.Equal(10m, reading.MonthCostUsd);
        Assert.True(reading.HasUsageThisMonth);
        Assert.Equal(40.0, reading.Percent); // hand-derived: 10m / 25m = 0.4 × 100 = 40
    }

    [Fact]
    public void Compute_E7_AgentIsTrimmedForTheReadingName_AndMatchesTheReportRow()
    {
        // E7: reading.Agent = agent.Trim().ToLowerInvariant(); a padded request still matches.
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 4m)));

        var reading = AgentBudget.Compute(report, "  OpenCode  ", Budgets(), Today);

        Assert.NotNull(reading);
        Assert.Equal("opencode", reading.Agent);
        Assert.Equal(4m, reading.MonthCostUsd);
    }

    [Theory]
    // Q1.2: "a budget <= 0 is treated as absent" → BudgetUsd null, Percent null.
    // (decimal cannot be an attribute argument — parsed from an invariant string.)
    [InlineData("0")]
    [InlineData("-5")]
    public void Compute_Q12_NonPositiveBudget_TreatedAsAbsent(string budgetText)
    {
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 5m)));
        var budgets = Budgets(("opencode", decimal.Parse(budgetText, CultureInfo.InvariantCulture)));

        var reading = AgentBudget.Compute(report, "opencode", budgets, Today);

        Assert.NotNull(reading);
        Assert.Null(reading.BudgetUsd);
        Assert.Null(reading.Percent);
    }

    // ── Q1.2 / E7: guards ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Compute_E7_BlankAgent_ThrowsArgumentExceptionWithParamNameAgent(string agent)
    {
        // E7: blank agent → ArgumentException with ParamName == "agent".
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 5m)));

        var ex = Assert.Throws<ArgumentException>(() => AgentBudget.Compute(report, agent, Budgets(), Today));
        Assert.Equal("agent", ex.ParamName);
    }

    [Fact]
    public void Compute_Q12_NullBudgets_ThrowsArgumentNullExceptionWithParamNameBudgets()
    {
        var report = NovemberReport(Usage("opencode", ("glm-4.6", 5m)));

        var ex = Assert.Throws<ArgumentNullException>(() => AgentBudget.Compute(report, "opencode", null!, Today));
        Assert.Equal("budgets", ex.ParamName);
    }
}
