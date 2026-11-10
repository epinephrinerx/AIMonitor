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
/// oracles O1–O5 and decision E7, the Q1.2 rules (month from today.Year/Month, only report.Monthly
/// is read, case-insensitive agent/budget match, budget &lt;= 0 absent, null report → null,
/// blank agent → ArgumentException, null budgets → ArgumentNullException, pure / never throws for a
/// valid report). Percent oracle maths are hand-derived in each test. Expected not to compile
/// until Q1 merges.
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
        // E7: month row = m.Year == today.Year && m.Month == today.Month. December rows of 2025 and
        // 2026 share the month number with each other but neither matches November 2026.
        var report = Report(
            new MonthlyAgentUsage(2025, 12, [Usage("opencode", ("glm-4.6", 5m))]),
            new MonthlyAgentUsage(2026, 12, [Usage("opencode", ("glm-4.6", 7m))]),
            new MonthlyAgentUsage(2026, 11, [Usage("opencode", ("glm-4.6", 2m))]));

        var reading = AgentBudget.Compute(report, "opencode", Budgets(), Today);

        Assert.NotNull(reading);
        Assert.Equal(2m, reading.MonthCostUsd);
        Assert.True(reading.HasUsageThisMonth);
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
