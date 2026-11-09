using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

/// <summary>
/// Tests for the aggregate records of SPEC R-014 §P1.1: <see cref="DailyAgentUsage"/>,
/// <see cref="MonthlyAgentUsage"/>, <see cref="SessionAgentUsage"/>, <see cref="AgentUsagePricing"/>
/// and <see cref="AgentUsageReport"/>. The spec lists the orderings under the report's definition,
/// and decision R10 confirms "AgentUsageReport itself normalizes in its constructor": daily by date
/// ascending, monthly by (year, month) ascending, sessions by LastActivity descending, UnpricedModels
/// distinct + ordinal sort. The four ordering/dedup report tests below are the red set against
/// 083120e (the reviewed record stores its lists as given).
/// </summary>
public class AgentUsageReportTests
{
    private static readonly DateTimeOffset BaseInstant = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static AgentUsage Agent(string id) => new(id, new TokenCounts(1, 1, 0, 0, 2), Array.Empty<ModelUsage>());

    private static DailyAgentUsage Daily(int year, int month, int day) =>
        new(new DateOnly(year, month, day), new[] { Agent("claude") });

    private static MonthlyAgentUsage Monthly(int year, int month) => new(year, month, new[] { Agent("claude") });

    private static SessionAgentUsage Session(string id, int hour) =>
        Session(id, new DateTimeOffset(2026, 11, 7, hour, 0, 0, TimeSpan.Zero));

    private static SessionAgentUsage Session(string id, DateTimeOffset lastActivity) =>
        new(id, lastActivity, Agent("claude"));

    private static AgentUsageReport Report(
        IReadOnlyList<DailyAgentUsage>? daily = null,
        IReadOnlyList<MonthlyAgentUsage>? monthly = null,
        IReadOnlyList<SessionAgentUsage>? sessions = null,
        IReadOnlyList<string>? unpricedModels = null,
        AgentUsagePricing pricing = AgentUsagePricing.OnlineRequested,
        DateTimeOffset? retrievedAt = null,
        int skippedRows = 0) =>
        new(
            daily ?? Array.Empty<DailyAgentUsage>(),
            monthly ?? Array.Empty<MonthlyAgentUsage>(),
            sessions ?? Array.Empty<SessionAgentUsage>(),
            unpricedModels ?? Array.Empty<string>(),
            pricing,
            retrievedAt ?? BaseInstant,
            skippedRows);

    [Fact]
    public void DailyAgentUsage_Ctor_PreservesDateAndAgents()
    {
        var agents = new[] { Agent("claude"), Agent("codex") };
        var daily = new DailyAgentUsage(new DateOnly(2026, 11, 7), agents);

        Assert.Equal(new DateOnly(2026, 11, 7), daily.Date);
        Assert.Equal(2, daily.Agents.Count);
        Assert.Equal("claude", daily.Agents[0].Agent);
        Assert.Equal("codex", daily.Agents[1].Agent);
    }

    [Fact]
    public void MonthlyAgentUsage_Ctor_PreservesYearMonthAndAgents()
    {
        var monthly = Monthly(2026, 11);

        Assert.Equal(2026, monthly.Year);
        Assert.Equal(11, monthly.Month);
        Assert.Single(monthly.Agents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    public void MonthlyAgentUsage_MonthOutside1To12_Throws(int month)
    {
        // SPEC P1.1: "MonthlyAgentUsage(int Year, int Month, …) (Month 1–12)".
        Assert.ThrowsAny<ArgumentException>(() => Monthly(2026, month));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void MonthlyAgentUsage_MonthBoundaryValues_AreAccepted(int month)
    {
        // SPEC P1.1: Month 1–12 inclusive.
        var monthly = Monthly(2026, month);

        Assert.Equal(month, monthly.Month);
    }

    [Fact]
    public void SessionAgentUsage_Ctor_PreservesIdActivityAndUsage()
    {
        var lastActivity = new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.FromHours(7));
        var usage = Agent("codex");
        var session = new SessionAgentUsage("abc123", lastActivity, usage);

        Assert.Equal("abc123", session.SessionId);
        Assert.Equal(lastActivity, session.LastActivity);
        Assert.Equal("codex", session.Usage.Agent);
    }

    [Fact]
    public void AgentUsagePricing_HasExactlyOnlineRequestedAndOffline()
    {
        // Decision OP3: "AgentUsagePricing becomes OnlineRequested / Offline" — exactly these two members.
        // Enum.GetValues ordering is not guaranteed, so compare ordinal-sorted names.
        var values = Enum.GetValues<AgentUsagePricing>().OrderBy(v => v.ToString(), StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { AgentUsagePricing.Offline, AgentUsagePricing.OnlineRequested }, values);
    }

    [Fact]
    public void Report_SortsDailyByDateAscending()
    {
        // SPEC P1.1: "Daily sorted by date ascending". Decision R10: "AgentUsageReport itself
        // normalizes in its constructor" — this record-level sort is the red expectation at 083120e
        // (the reviewed record stores the list as given; only the parser sorts).
        var daily = new[] { Daily(2026, 11, 3), Daily(2026, 11, 1), Daily(2026, 11, 2) };

        var report = Report(daily: daily);

        Assert.Equal(
            new[] { new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3) },
            report.Daily.Select(d => d.Date));
    }

    [Fact]
    public void Report_SortsMonthlyByYearThenMonthAscending()
    {
        // SPEC P1.1: "Monthly by (year, month) ascending". Decision R10: normalised by the record.
        var monthly = new[] { Monthly(2026, 1), Monthly(2025, 12), Monthly(2026, 3) };

        var report = Report(monthly: monthly);

        Assert.Equal(
            new[] { (2025, 12), (2026, 1), (2026, 3) },
            report.Monthly.Select(m => (m.Year, m.Month)));
    }

    [Fact]
    public void Report_SortsSessionsByLastActivityDescending()
    {
        // SPEC P1.1: "Sessions by LastActivity descending". Decision R10: normalised by the record.
        var sessions = new[] { Session("early", 9), Session("latest", 11), Session("middle", 10) };

        var report = Report(sessions: sessions);

        Assert.Equal(new[] { "latest", "middle", "early" }, report.Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Report_UnpricedModels_AreDistinctAndOrdinalSorted()
    {
        // SPEC P1.1: "UnpricedModels distinct, ordinal-sorted". Decision R10: the record applies this
        // normalisation, so an unsorted, duplicated input list reads back sorted and distinct.
        // Ordinal order puts upper case before lower case: "Alpha" < "alpha" < "zeta" (code points 65 < 97 < 122).
        var report = Report(unpricedModels: new[] { "zeta", "Alpha", "alpha", "zeta" });

        Assert.Equal(new[] { "Alpha", "alpha", "zeta" }, report.UnpricedModels);
    }

    [Fact]
    public void Report_PreservesPricingRetrievedAtAndSkippedRows()
    {
        // SPEC P1.1 report signature: Pricing, RetrievedAt and SkippedRows are stored as given.
        var when = BaseInstant.AddMinutes(5);
        var report = Report(pricing: AgentUsagePricing.Offline, retrievedAt: when, skippedRows: 3);

        Assert.Equal(AgentUsagePricing.Offline, report.Pricing);
        Assert.Equal(when, report.RetrievedAt);
        Assert.Equal(3, report.SkippedRows);
    }

    [Fact]
    public void Report_EmptySections_AreValid()
    {
        // SPEC P1.3 structural rule: each section "may be empty"; P1.1 puts no non-empty requirement on the report.
        var report = Report();

        Assert.Empty(report.Daily);
        Assert.Empty(report.Monthly);
        Assert.Empty(report.Sessions);
        Assert.Empty(report.UnpricedModels);
        Assert.Equal(0, report.SkippedRows);
    }
}
