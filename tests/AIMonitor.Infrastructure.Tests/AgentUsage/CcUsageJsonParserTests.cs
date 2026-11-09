using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.AgentUsage;

namespace AIMonitor.Infrastructure.Tests.AgentUsage;

/// <summary>
/// Tests for <see cref="CcUsageJsonParser"/> per SPEC R-014 §P1.3 "Parser", decisions A3–A6 and the
/// P1.4 must-cover list. Every numeric expectation cites the spec line or shows the hand calculation
/// (§5.6 item 2). All JSON is synthetic; nothing here runs the real ccusage.
/// </summary>
public sealed class CcUsageJsonParserTests
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    // Valid claude agent object. Hand calc: buckets 100 + 200 + 300 + 350 = 950; total 1000 →
    // Other = 50 (the SPEC P1.4 oracle shape). The model's bucket sum is 60 + 80 + 20 + 40 = 200.
    private const string ClaudeAgent =
        """{"agent":"claude","inputTokens":100,"outputTokens":200,"cacheCreationTokens":300,"cacheReadTokens":350,"totalTokens":1000,"modelBreakdowns":[{"modelName":"claude-opus-4-8","inputTokens":60,"outputTokens":80,"cacheCreationTokens":20,"cacheReadTokens":40,"cost":0.0425}]}""";

    // Token fields shared by helper-built rows: buckets 1 + 2 + 3 + 4 = 10 = total → Other = 0.
    private const string AgentTokens =
        "\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":10";

    // A valid model object (model Total is its bucket sum by definition; SPEC P1.3).
    // Hand calc: 10 + 20 + 0 + 0 = 30.
    private const string ValidModel =
        """{"modelName":"claude-opus-4-8","inputTokens":10,"outputTokens":20,"cacheCreationTokens":0,"cacheReadTokens":0,"cost":0.0075}""";

    private static AgentUsageReport Parse(string json, AgentUsagePricing pricing = AgentUsagePricing.OnlineRequested) =>
        CcUsageJsonParser.Parse(json, pricing, RetrievedAt);

    private static void AssertInvalidResponse(string json)
    {
        // Structural violation → "InvalidResponse, whole report rejected" (SPEC P1.3).
        var error = Assert.Throws<AgentUsageUnavailableException>(() => Parse(json));
        Assert.Equal(AgentUsageErrorKind.InvalidResponse, error.Kind);
    }

    private static string Root(string daily = "[]", string monthly = "[]", string session = "[]", string? totals = null)
        => totals is null
            ? $"{{\"daily\":[{daily}],\"monthly\":[{monthly}],\"session\":[{session}]}}"
            : $"{{\"daily\":[{daily}],\"monthly\":[{monthly}],\"session\":[{session}],\"totals\":{totals}}}";

    private static string DailyRow(string period, string agents) =>
        $"{{\"period\":\"{period}\",\"agents\":[{agents}]}}";

    private static string GoodDailyRow() => DailyRow("2026-11-06", ClaudeAgent);

    private static string MonthlyRow(string period, string agents) =>
        $"{{\"period\":\"{period}\",\"agents\":[{agents}]}}";

    private static string AgentWithTokens(string tokensJson) =>
        $"{{\"agent\":\"codex\",{tokensJson},\"modelBreakdowns\":[{ValidModel}]}}";

    private static string ModelWith(string modelInner) => "{" + modelInner + "}";

    private static string SessionRow(
        string id = "\"period\":\"s-1\"",
        string agent = "\"agent\":\"claude\"",
        string lastActivity = "\"metadata\":{\"lastActivity\":\"2026-11-07T10:00:00Z\"}")
    {
        var parts = new List<string>();
        if (id.Length > 0) parts.Add(id);
        if (agent.Length > 0) parts.Add(agent);
        // Row tokens: buckets 10 + 20 + 0 + 0 = 30 = total → Other = 0.
        parts.Add("\"inputTokens\":10");
        parts.Add("\"outputTokens\":20");
        parts.Add("\"cacheCreationTokens\":0");
        parts.Add("\"cacheReadTokens\":0");
        parts.Add("\"totalTokens\":30");
        parts.Add($"\"modelBreakdowns\":[{ValidModel}]");
        if (lastActivity.Length > 0) parts.Add(lastActivity);
        return "{" + string.Join(",", parts) + "}";
    }

    private static string Many(string row, int count) => string.Join(",", Enumerable.Repeat(row, count));

    private static string NestedEmptyObjects(int count) =>
        string.Concat(Enumerable.Repeat("{", count)) + string.Concat(Enumerable.Repeat("}", count));

    // ---------------------------------------------------------------- structural rules (P1.3)

    [Theory]
    [InlineData("[]")]                       // root is not an object
    [InlineData("42")]                       // root is not an object
    [InlineData("not json at all")]          // unparseable
    [InlineData("")]                         // empty input
    public void Parse_RootNotObject_ThrowsInvalidResponse(string json) => AssertInvalidResponse(json);

    [Fact]
    public void Parse_MissingAnyOfTheThreeSections_ThrowsInvalidResponse()
    {
        // SPEC P1.3: "Root is an object containing arrays daily, monthly, session (each may be empty)."
        AssertInvalidResponse("""{"monthly":[],"session":[]}""");   // no daily
        AssertInvalidResponse("""{"daily":[],"session":[]}""");     // no monthly
        AssertInvalidResponse("""{"daily":[],"monthly":[]}""");     // no session
    }

    [Fact]
    public void Parse_SectionPresentButNotAnArray_ThrowsInvalidResponse()
    {
        // SPEC P1.3: the root must "contain arrays daily, monthly, session"; a non-array section
        // violates the structural rule → whole report rejected.
        AssertInvalidResponse("""{"daily":42,"monthly":[],"session":[]}""");
    }

    [Fact]
    public void Parse_AllSectionsEmpty_ReturnsEmptyReportWithoutRejecting()
    {
        // SPEC P1.3: "each may be empty"; the all-rows-skipped rejection only fires "if at least one
        // row existed", and here zero rows exist.
        var report = Parse(Root());

        Assert.Empty(report.Daily);
        Assert.Empty(report.Monthly);
        Assert.Empty(report.Sessions);
        Assert.Empty(report.UnpricedModels);
        Assert.Equal(0, report.SkippedRows);
    }

    [Fact]
    public void Parse_MoreThanTwentyThousandRowsInOneSection_ThrowsInvalidResponse()
    {
        // SPEC P1.3: "More than 20,000 rows in any section … → InvalidResponse".
        // 20,001 > 20,000.
        AssertInvalidResponse(Root(daily: Many(GoodDailyRow(), 20_001)));
    }

    [Fact]
    public void Parse_ExactlyTwentyThousandRowsInOneSection_IsAccepted()
    {
        // Boundary of "More than 20,000": 20,000 is not more than 20,000, so the report is accepted.
        var report = Parse(Root(daily: Many(GoodDailyRow(), 20_000)));

        Assert.Equal(20_000, report.Daily.Count);
        Assert.Equal(0, report.SkippedRows);
    }

    [Fact]
    public void Parse_MoreThanFiveHundredModelBreakdownsInOneRow_ThrowsInvalidResponse()
    {
        // SPEC P1.3: "more than 500 modelBreakdowns in one row → InvalidResponse". 501 > 500.
        var agent = $"{{\"agent\":\"claude\",{AgentTokens},\"modelBreakdowns\":[{Many(ValidModel, 501)}]}}";

        AssertInvalidResponse(Root(daily: DailyRow("2026-11-05", agent)));
    }

    [Fact]
    public void Parse_ExactlyFiveHundredModelBreakdownsInOneRow_IsAccepted()
    {
        // Boundary of "more than 500": 500 breakdowns → row kept, not rejected.
        var agent = $"{{\"agent\":\"claude\",{AgentTokens},\"modelBreakdowns\":[{Many(ValidModel, 500)}]}}";

        var report = Parse(Root(daily: DailyRow("2026-11-05", agent)));

        Assert.Single(report.Daily);
        Assert.Equal(500, report.Daily[0].Agents[0].Models.Count);
    }

    [Fact]
    public void Parse_NestingBeyondMaxJsonDepth64_ThrowsInvalidResponse()
    {
        // SPEC P1.3: "Max JSON depth 64." Depth convention here: the root object is level 1, so an
        // "x" value of 64 further nested objects reaches level 65 → beyond the limit.
        AssertInvalidResponse(
            "{" + "\"daily\":[" + ClaudeRowInRoot() + "],\"monthly\":[],\"session\":[],\"x\":" + NestedEmptyObjects(64) + "}");
    }

    [Fact]
    public void Parse_NestingAtMaxJsonDepth64_IsAccepted()
    {
        // 63 further nested objects under the root (root = level 1) reaches exactly level 64 → allowed.
        var report = Parse(
            "{" + "\"daily\":[" + ClaudeRowInRoot() + "],\"monthly\":[],\"session\":[],\"x\":" + NestedEmptyObjects(63) + "}");

        Assert.Single(report.Daily);
    }

    private static string ClaudeRowInRoot() => DailyRow("2026-11-05", ClaudeAgent);

    // ---------------------------------------------------------------- daily row rules

    [Fact]
    public void Parse_ValidDailyRow_KeepsAgentTokensOtherAndModelDetails()
    {
        // SPEC P1.3: period → date; use daily[i].agents[]; agent fields parsed.
        // Row carries a row-level agent:"all" with totals 999999 that must be ignored (derived).
        var json = Root(daily:
            $$"""{"period":"2026-11-05","agent":"all","inputTokens":999999,"outputTokens":999999,"cacheCreationTokens":999999,"cacheReadTokens":999999,"totalTokens":999999,"agents":[{{ClaudeAgent}}]}""");

        var report = Parse(json);

        Assert.Equal(0, report.SkippedRows);
        var row = Assert.Single(report.Daily);
        Assert.Equal(new DateOnly(2026, 11, 5), row.Date);
        // The row-level "all" agent is not materialised: exactly the one agent from agents[].
        var claude = Assert.Single(row.Agents);
        Assert.Equal("claude", claude.Agent);
        Assert.Equal(100, claude.Tokens.Input);
        Assert.Equal(200, claude.Tokens.Output);
        Assert.Equal(300, claude.Tokens.CacheCreation);
        Assert.Equal(350, claude.Tokens.CacheRead);
        Assert.Equal(1000, claude.Tokens.Total);
        Assert.Equal(50, claude.Tokens.Other); // 1000 − (100 + 200 + 300 + 350 = 950) = 50 (P1.4 oracle shape)
        Assert.DoesNotContain(row.Agents, a => a.Tokens.Total == 999999); // row-level totals ignored

        var model = Assert.Single(claude.Models);
        Assert.Equal("claude-opus-4-8", model.Model); // kept exactly as ccusage printed it (P1.1)
        // Model Total = bucket sum (SPEC P1.3): 60 + 80 + 20 + 40 = 200.
        Assert.Equal(200, model.Tokens.Total);
        Assert.Equal(0.0425m, model.CostUsd);
        Assert.Equal(0.0425m, claude.PricedCostUsd); // only priced model
        Assert.False(claude.HasUnpricedModels);
    }

    [Theory]
    [InlineData("2026-02-30")]              // A4: not a real calendar date → row skipped
    [InlineData("2026-13-01")]              // month 13 → row skipped (A4)
    [InlineData("2026-11-8")]               // not zero-padded yyyy-MM-dd → no match
    [InlineData("2026-11-08T00:00:00Z")]    // extra characters → no match
    [InlineData("06-11-2026")]              // wrong order → no match
    [InlineData("20261106")]                // no separators → no match
    [InlineData("nov-06-2026")]             // text → no match
    [InlineData("")]                        // blank → no match
    public void Parse_DailyPeriodNotInvariantDate_SkipsRow(string period)
    {
        // SPEC P1.3: "daily[i].period matches yyyy-MM-dd (invariant, real calendar date)";
        // violation → "row skipped, SkippedRows += 1". A valid sibling row prevents the
        // all-rows-skipped rejection so the skip itself is under test.
        var json = Root(daily: string.Join(",", DailyRow(period, ClaudeAgent), GoodDailyRow()));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        var row = Assert.Single(report.Daily);
        Assert.Equal(new DateOnly(2026, 11, 6), row.Date);
    }

    [Fact]
    public void Parse_DailyRowMissingPeriod_SkipsRow()
    {
        // SPEC P1.3: the rule is "daily[i].period matches yyyy-MM-dd"; without the field there is no
        // match → row skipped. The valid sibling prevents the all-rows-skipped rejection so the skip
        // itself is under test.
        var json = Root(daily: string.Join(",",
            $$"""{"agents":[{{ClaudeAgent}}]}""",
            GoodDailyRow()));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal(new DateOnly(2026, 11, 6), Assert.Single(report.Daily).Date);
    }

    [Fact]
    public void Parse_DailyRowWithoutAgentsArray_SkipsRow()
    {
        // SPEC P1.3: "Row without an agents array → skipped" (valid sibling keeps the report alive).
        var json = Root(daily: string.Join(",",
            """{"period":"2026-11-05"}""",
            GoodDailyRow()));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal(new DateOnly(2026, 11, 6), Assert.Single(report.Daily).Date);
    }

    [Fact]
    public void Parse_DailyRowAgentsFieldNotAnArray_SkipsRow()
    {
        // SPEC P1.3: rows must "use daily[i].agents[]"; a non-array agents field is not an agents
        // array, so the row is skipped (same rule as a missing array — flagged as an assumption).
        var json = Root(daily: string.Join(",",
            """{"period":"2026-11-05","agents":42}""",
            GoodDailyRow()));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal(new DateOnly(2026, 11, 6), Assert.Single(report.Daily).Date);
    }

    [Fact]
    public void Parse_AgentIdIsTrimmedAndLowerCased()
    {
        // SPEC P1.1 (applied by the parser through the domain record): "Agent = ccusage agent id,
        // trimmed, lower-case invariant".
        var json = Root(daily: DailyRow("2026-11-05", ClaudeAgent.Replace("\"agent\":\"claude\"", """ "agent":"  ANTIGRAVITY " """)));

        var report = Parse(json);

        Assert.Equal("antigravity", report.Daily[0].Agents[0].Agent);
    }

    // ---------------------------------------------------------------- monthly row rules

    [Fact]
    public void Parse_ValidMonthlyRow_KeepsYearMonthAndAgents()
    {
        // SPEC P1.3: "monthly[i].period matches yyyy-MM; same agents[] rule".
        var json = Root(monthly: MonthlyRow("2026-11", ClaudeAgent));

        var report = Parse(json);

        var row = Assert.Single(report.Monthly);
        Assert.Equal(2026, row.Year);
        Assert.Equal(11, row.Month);
        Assert.Equal("claude", row.Agents[0].Agent);
    }

    [Theory]
    [InlineData("2026-13")]   // A4: "2026-13 → row skipped" (month must be 1–12)
    [InlineData("2026-00")]   // month 0 → outside 1–12
    [InlineData("2026-1")]    // not zero-padded yyyy-MM
    [InlineData("2026/11")]   // wrong separator
    [InlineData("202611")]    // no separator
    [InlineData("2026-11-05")]// full date is not yyyy-MM
    public void Parse_MonthlyPeriodBad_SkipsRow(string period)
    {
        var json = Root(monthly: string.Join(",", MonthlyRow(period, ClaudeAgent), MonthlyRow("2026-10", ClaudeAgent)));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        var row = Assert.Single(report.Monthly);
        Assert.Equal((2026, 10), (row.Year, row.Month));
    }

    [Fact]
    public void Parse_MonthlyRowWithoutAgentsArray_SkipsRow()
    {
        // SPEC P1.3: "same agents[] rule" as daily rows (valid sibling keeps the report alive).
        var json = Root(monthly: string.Join(",",
            """{"period":"2026-11"}""",
            MonthlyRow("2026-10", ClaudeAgent)));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        var row = Assert.Single(report.Monthly);
        Assert.Equal((2026, 10), (row.Year, row.Month));
    }

    // ---------------------------------------------------------------- session row rules (P1.3 + A5)

    [Fact]
    public void Parse_SessionRow_TheRowItselfBecomesTheAgentUsage()
    {
        // SPEC P1.3: "session[i]: … the row itself is the AgentUsage (no agents[])".
        // Row tokens 10 + 20 + 0 + 0 = 30 = total → Other 0; model Total = bucket sum = 30.
        var report = Parse(Root(session: SessionRow()));

        var session = Assert.Single(report.Sessions);
        Assert.Equal("s-1", session.SessionId);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero), session.LastActivity);
        Assert.Equal("claude", session.Usage.Agent);
        Assert.Equal(30, session.Usage.Tokens.Total);
        Assert.Equal(0, session.Usage.Tokens.Other);
        var model = Assert.Single(session.Usage.Models);
        Assert.Equal(0.0075m, model.CostUsd);
        Assert.Equal(0.0075m, session.Usage.PricedCostUsd);
    }

    [Fact]
    public void Parse_SessionLastActivityWithOffset_PreservesTheInstant()
    {
        // SPEC P1.3: metadata.lastActivity "ISO-8601 with offset or Z". 12:00+07:00 is the instant
        // 05:00Z (hand calc: 12:00 − 7:00 = 05:00).
        var report = Parse(Root(session: SessionRow(lastActivity: """ "metadata":{"lastActivity":"2026-11-07T12:00:00+07:00"} """)));

        Assert.Equal(new DateTimeOffset(2026, 11, 7, 5, 0, 0, TimeSpan.Zero), report.Sessions[0].LastActivity);
    }

    [Theory]
    [InlineData("")]                                            // A5: metadata missing
    [InlineData("\"metadata\":{}")]                              // A5: lastActivity missing
    [InlineData("\"metadata\":{\"lastActivity\":\"2026-11-07T10:00:00\"}")] // A5: no offset/Z
    [InlineData("\"metadata\":{\"lastActivity\":\"yesterday\"}")]// not ISO-8601
    [InlineData("\"metadata\":{\"lastActivity\":null}")]         // null, not a string
    public void Parse_SessionBadLastActivity_SkipsRow(string metadataField)
    {
        // SPEC P1.3 + decision A5: session row with missing metadata / missing lastActivity / no
        // offset → row skipped. The valid sibling prevents the all-rows-skipped rejection.
        var json = Root(session: string.Join(",", SessionRow(lastActivity: metadataField), SessionRow(id: "\"period\":\"s-good\"")));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal("s-good", Assert.Single(report.Sessions).SessionId);
    }

    [Theory]
    [InlineData("\"period\":\"\"")]              // blank id → "non-blank string" violated
    [InlineData("\"period\":123")]               // id must be a string
    [InlineData("")]                            // period missing entirely
    public void Parse_SessionBadId_SkipsRow(string idField)
    {
        // SPEC P1.3: "period = session id (non-blank string ≤ 256 chars)".
        var json = Root(session: string.Join(",", SessionRow(id: idField), SessionRow(id: "\"period\":\"s-good\"")));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal("s-good", Assert.Single(report.Sessions).SessionId);
    }

    [Fact]
    public void Parse_SessionIdExactly256Characters_IsAccepted()
    {
        // SPEC P1.3 boundary: "≤ 256 chars" → 256 is allowed.
        var id = new string('s', 256);

        var report = Parse(Root(session: SessionRow(id: $"\"period\":\"{id}\"")));

        Assert.Equal(id, Assert.Single(report.Sessions).SessionId);
        Assert.Equal(0, report.SkippedRows);
    }

    [Fact]
    public void Parse_SessionId257Characters_SkipsRow()
    {
        // SPEC P1.3 boundary: 257 > 256 → row skipped (valid sibling keeps the report alive).
        var tooLong = new string('s', 257);

        var report = Parse(Root(session: string.Join(",",
            SessionRow(id: $"\"period\":\"{tooLong}\""),
            SessionRow(id: "\"period\":\"s-good\""))));

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal("s-good", Assert.Single(report.Sessions).SessionId);
    }

    [Fact]
    public void Parse_SessionRowWithoutAgent_SkipsRow()
    {
        // SPEC P1.3: session row fields include "agent string"; a row with no agent string cannot
        // build the AgentUsage → skipped (assumption: missing field counts as violation, same as
        // the token-field "missing → invalid row" rule).
        var json = Root(session: string.Join(",", SessionRow(agent: ""), SessionRow(id: "\"period\":\"s-good\"")));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal("s-good", Assert.Single(report.Sessions).SessionId);
    }

    // ---------------------------------------------------------------- token field rules

    [Theory]
    [InlineData("\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":10")]                        // inputTokens missing
    [InlineData("\"inputTokens\":1,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":10")]                        // outputTokens missing
    [InlineData("\"inputTokens\":1,\"outputTokens\":2,\"cacheReadTokens\":4,\"totalTokens\":10")]                               // cacheCreationTokens missing
    [InlineData("\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":3,\"totalTokens\":10")]                           // cacheReadTokens missing
    [InlineData("\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4")]                        // totalTokens missing
    [InlineData("\"inputTokens\":-1,\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":10")]    // negative
    [InlineData("\"inputTokens\":1.5,\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":10")]   // fractional
    [InlineData("\"inputTokens\":\"1\",\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":10")] // string
    [InlineData("\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":9")]      // total 9 < bucket sum 1+2+3+4 = 10
    [InlineData("\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":3,\"cacheReadTokens\":4,\"totalTokens\":9007199254740992")] // total 2^53 > 2^53 − 1 max
    public void Parse_InvalidAgentTokenField_SkipsRow(string tokensJson)
    {
        // SPEC P1.3: "Token fields …: JSON integers 0 ≤ n ≤ 2^53 − 1. Missing, fractional, negative,
        // string → invalid row. totalTokens < bucket sum → invalid row."
        var json = Root(daily: string.Join(",", DailyRow("2026-11-05", AgentWithTokens(tokensJson)), GoodDailyRow()));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal(new DateOnly(2026, 11, 6), Assert.Single(report.Daily).Date);
    }

    [Fact]
    public void Parse_TokenAtMaxAllowed2Pow53Minus1_IsAccepted()
    {
        // SPEC P1.3 boundary: "0 ≤ n ≤ 2^53 − 1"; 2^53 − 1 = 9007199254740991 is allowed.
        // Buckets are 0, so Total ≥ bucket sum (0 + 0 + 0 + 0 = 0 ≤ 9007199254740991) holds; Other
        // = 9007199254740991 − 0 = 9007199254740991.
        var tokens = "\"inputTokens\":0,\"outputTokens\":0,\"cacheCreationTokens\":0,\"cacheReadTokens\":0,\"totalTokens\":9007199254740991";
        var agent = $"{{\"agent\":\"codex\",{tokens},\"modelBreakdowns\":[{ValidModel}]}}";

        var report = Parse(Root(daily: DailyRow("2026-11-05", agent)));

        Assert.Equal(9007199254740991L, report.Daily[0].Agents[0].Tokens.Total);
        Assert.Equal(0, report.SkippedRows);
    }

    [Fact]
    public void Parse_AgentTokensComeFromTheAgentObjectNotFromSummingModels()
    {
        // SPEC P1.3: "Agent Tokens come from the agent object's own fields, not from summing models."
        // Agent buckets 100/200/300/350 (sum 950, total 1000) vs a model with 10/20/0/0.
        var model = """{"modelName":"tiny","inputTokens":10,"outputTokens":20,"cacheCreationTokens":0,"cacheReadTokens":0,"cost":0.01}""";
        var agent = $"{{\"agent\":\"claude\",\"inputTokens\":100,\"outputTokens\":200,\"cacheCreationTokens\":300,\"cacheReadTokens\":350,\"totalTokens\":1000,\"modelBreakdowns\":[{model}]}}";

        var report = Parse(Root(daily: DailyRow("2026-11-05", agent)));

        var claude = report.Daily[0].Agents[0];
        Assert.Equal(100, claude.Tokens.Input);   // not the model's 10
        Assert.Equal(200, claude.Tokens.Output);  // not the model's 20
        Assert.Equal(50, claude.Tokens.Other);    // 1000 − 950 = 50
    }

    // ---------------------------------------------------------------- model breakdown rules

    [Fact]
    public void Parse_MissingPricing_MakesCostNullRegardlessOfCost_AndExcludedFromPricedCostUsd()
    {
        // SPEC P1.4 oracle: "missingPricing → null cost and excluded from PricedCostUsd (oracle:
        // models 1.25 + unknown → 1.25, HasUnpricedModels)".
        // Hand calc: 1.25m + (null) → PricedCostUsd = 1.25m.
        var missing = """{"modelName":"gemini-3","inputTokens":5,"outputTokens":5,"cacheCreationTokens":0,"cacheReadTokens":0,"cost":0.75,"missingPricing":true}""";
        var priced = """{"modelName":"claude-opus-4-8","inputTokens":5,"outputTokens":5,"cacheCreationTokens":0,"cacheReadTokens":0,"cost":1.25}""";
        var agent = $"{{\"agent\":\"antigravity\",\"inputTokens\":10,\"outputTokens\":10,\"cacheCreationTokens\":0,\"cacheReadTokens\":0,\"totalTokens\":20,\"modelBreakdowns\":[{priced},{missing}]}}";

        var report = Parse(Root(daily: DailyRow("2026-11-05", agent)));

        var antigravity = report.Daily[0].Agents[0];
        Assert.Equal(1.25m, antigravity.PricedCostUsd);
        Assert.True(antigravity.HasUnpricedModels);
        var unknown = antigravity.Models.Single(m => m.Model == "gemini-3");
        Assert.Null(unknown.CostUsd); // "missingPricing: true → CostUsd = null regardless of cost"
        Assert.Equal(1.25m, antigravity.Models.Single(m => m.Model == "claude-opus-4-8").CostUsd);
    }

    [Theory]
    [InlineData("\"modelName\":\"\",\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":0,\"cacheReadTokens\":0,\"cost\":0.01")]  // blank name
    [InlineData("\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":0,\"cacheReadTokens\":0,\"cost\":0.01")]                     // name missing
    [InlineData("\"modelName\":\"m\",\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":0,\"cacheReadTokens\":0,\"cost\":-0.01")] // negative cost
    [InlineData("\"modelName\":\"m\",\"inputTokens\":1,\"outputTokens\":2,\"cacheCreationTokens\":0,\"cacheReadTokens\":0,\"cost\":\"0.01\"")] // cost not a number
    [InlineData("\"modelName\":\"m\",\"inputTokens\":-1,\"outputTokens\":2,\"cacheCreationTokens\":0,\"cacheReadTokens\":0,\"cost\":0.01")]   // negative token field
    public void Parse_InvalidModelEntry_InvalidatesItsRow(string modelInner)
    {
        // SPEC P1.3: "modelBreakdowns[j]: modelName non-blank; same four token fields (no
        // totalTokens here …); cost finite number ≥ 0 parsed as decimal … An invalid model entry
        // invalidates its row."
        // (A present-but-redundant model "totalTokens" is not tested: the spec does not say whether
        // it is an ignored extra property or a violation — reported to the Coding Owner.)
        var agent = $"{{\"agent\":\"claude\",{AgentTokens},\"modelBreakdowns\":[{ModelWith(modelInner)}]}}";
        var json = Root(daily: string.Join(",", DailyRow("2026-11-05", agent), GoodDailyRow()));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Equal(new DateOnly(2026, 11, 6), Assert.Single(report.Daily).Date);
    }

    // ---------------------------------------------------------------- totals rules (A3)

    [Fact]
    public void Parse_TotalsUnpricedModels_AreDistinctAndOrdinalSorted()
    {
        // SPEC P1.1 report rule: "UnpricedModels distinct, ordinal-sorted".
        // Ordinal: "Alpha" (A=65) < "alpha" (a=97) < "zeta" (z=122); the duplicate "zeta" collapses.
        var json = Root(totals: """{"unpricedModels":["zeta","Alpha","alpha","zeta"]}""");

        var report = Parse(json);

        Assert.Equal(new[] { "Alpha", "alpha", "zeta" }, report.UnpricedModels);
    }

    [Fact]
    public void Parse_TotalsMissing_YieldsNoUnpricedModelsAndIsNotAnError()
    {
        // Decision A3: "totals missing or not an object → treated as no unpriced models (not an error)".
        var report = Parse(Root(daily: GoodDailyRow()));

        Assert.Empty(report.UnpricedModels);
        Assert.Single(report.Daily);
    }

    [Fact]
    public void Parse_TotalsNotAnObject_YieldsNoUnpricedModelsAndIsNotAnError()
    {
        // Decision A3 verbatim case: totals present but not an object.
        var report = Parse(Root(totals: "\"oops\""));

        Assert.Empty(report.UnpricedModels);
    }

    [Fact]
    public void Parse_UnpricedModelsFieldElsewhere_IsIgnored()
    {
        // Decision A3: unpricedModels "is read from root.totals.unpricedModels only" — the same
        // field name inside a daily row must not leak into the report.
        var json = Root(daily:
            $$"""{"period":"2026-11-06","unpricedModels":["ghost-model"],"agents":[{{ClaudeAgent}}]}""");

        var report = Parse(json);

        Assert.Empty(report.UnpricedModels);
        Assert.Single(report.Daily);
    }

    // ---------------------------------------------------------------- ordering (P1.1 + P1.4)

    [Fact]
    public void Parse_DailyRowsAreSortedByDateAscending()
    {
        // SPEC P1.1: "Daily sorted by date ascending"; P1.4 requires the parser output ordering.
        var json = Root(daily: string.Join(",",
            DailyRow("2026-11-03", ClaudeAgent),
            DailyRow("2026-11-01", ClaudeAgent),
            DailyRow("2026-11-02", ClaudeAgent)));

        var report = Parse(json);

        Assert.Equal(
            new[] { new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3) },
            report.Daily.Select(d => d.Date));
    }

    [Fact]
    public void Parse_MonthlyRowsAreSortedByYearThenMonthAscending()
    {
        // SPEC P1.1: "Monthly by (year, month) ascending".
        var json = Root(monthly: string.Join(",",
            MonthlyRow("2026-01", ClaudeAgent),
            MonthlyRow("2025-12", ClaudeAgent),
            MonthlyRow("2026-03", ClaudeAgent)));

        var report = Parse(json);

        Assert.Equal(
            new[] { (2025, 12), (2026, 1), (2026, 3) },
            report.Monthly.Select(m => (m.Year, m.Month)));
    }

    [Fact]
    public void Parse_SessionRowsAreSortedByLastActivityDescending()
    {
        // SPEC P1.1: "Sessions by LastActivity descending"; P1.4 must-cover "session ordering".
        // Instants: 10:30Z > 09:00Z > 12:00+07:00 = 05:00Z (hand calc 12:00 − 7 h = 05:00Z).
        var json = Root(session: string.Join(",",
            SessionRow(id: """ "period":"s-morning" """, lastActivity: """ "metadata":{"lastActivity":"2026-11-07T09:00:00Z"} """),
            SessionRow(id: """ "period":"s-latest" """, lastActivity: """ "metadata":{"lastActivity":"2026-11-07T10:30:00Z"} """),
            SessionRow(id: """ "period":"s-offset" """, lastActivity: """ "metadata":{"lastActivity":"2026-11-07T12:00:00+07:00"} """)));

        var report = Parse(json);

        Assert.Equal(new[] { "s-latest", "s-morning", "s-offset" }, report.Sessions.Select(s => s.SessionId));
    }

    // ---------------------------------------------------------------- skipped-row accounting

    [Fact]
    public void Parse_MixedSections_CountsEverySkippedRowOnce()
    {
        // SPEC P1.3 row rules: each skipped row increments SkippedRows by 1, across all sections.
        // 4 invalid rows: bad daily period, daily without agents, bad monthly period, session
        // without metadata; 3 valid rows: daily 2026-11-05, monthly 2026-10, session s-good.
        var json = Root(
            daily: string.Join(",",
                DailyRow("2026-02-30", ClaudeAgent),          // A4: fake calendar date
                DailyRow("2026-11-05", ClaudeAgent),          // valid
                """{"period":"2026-11-04"}"""),              // no agents array
            monthly: string.Join(",",
                MonthlyRow("2026-13", ClaudeAgent),           // A4: month 13
                MonthlyRow("2026-10", ClaudeAgent)),          // valid
            session: string.Join(",",
                SessionRow(lastActivity: ""),                 // A5: metadata missing
                SessionRow(id: "\"period\":\"s-good\"")));    // valid

        var report = Parse(json);

        Assert.Equal(4, report.SkippedRows);
        Assert.Equal(new DateOnly(2026, 11, 5), Assert.Single(report.Daily).Date);
        Assert.Equal((2026, 10), (Assert.Single(report.Monthly).Year, Assert.Single(report.Monthly).Month));
        Assert.Equal("s-good", Assert.Single(report.Sessions).SessionId);
    }

    [Fact]
    public void Parse_EveryRowOfEverySectionSkipped_ThrowsInvalidResponse()
    {
        // SPEC P1.3: "if every row of every section was skipped and at least one row existed →
        // InvalidResponse". Here 2 rows exist (both daily) and both are invalid.
        var json = Root(daily: string.Join(",",
            DailyRow("2026-02-30", ClaudeAgent),
            """{"period":"2026-11-04"}"""));

        AssertInvalidResponse(json);
    }

    [Fact]
    public void Parse_OneSectionAllSkippedAnotherSectionValid_StillProducesTheReport()
    {
        // Contrast: the rejection needs *every* section's rows skipped. Daily all-skipped (1 row)
        // but the monthly row is valid → report with SkippedRows 1.
        var json = Root(
            daily: DailyRow("2026-13-01", ClaudeAgent),
            monthly: MonthlyRow("2026-11", ClaudeAgent));

        var report = Parse(json);

        Assert.Equal(1, report.SkippedRows);
        Assert.Empty(report.Daily);
        Assert.Single(report.Monthly);
    }

    // ---------------------------------------------------------------- unknown properties

    [Fact]
    public void Parse_UnknownExtraProperties_AreIgnoredEverywhere()
    {
        // SPEC P1.3: "Unknown extra properties are ignored everywhere." Extras are added at root,
        // row, agent and model level; the row still parses with zero skips.
        var agentWithExtras =
            $$"""{"agent":"claude","inputTokens":100,"outputTokens":200,"cacheCreationTokens":300,"cacheReadTokens":350,"totalTokens":1000,"unknownFlag":true,"modelBreakdowns":[{"modelName":"claude-opus-4-8","inputTokens":60,"outputTokens":80,"cacheCreationTokens":20,"cacheReadTokens":40,"cost":0.0425,"unknownCost":null}]}""";
        var json =
            $$"""{"daily":[{"period":"2026-11-05","unknownRow":"x","agents":[{{agentWithExtras}}]}],"monthly":[],"session":[],"unknownRoot":42}""";

        var report = Parse(json);

        Assert.Equal(0, report.SkippedRows);
        var row = Assert.Single(report.Daily);
        // Hand calc: 1000 − (100 + 200 + 300 + 350 = 950) = 50 → Other, unchanged by the extras.
        Assert.Equal(50, row.Agents[0].Tokens.Other);
        Assert.Equal(0.0425m, row.Agents[0].Models[0].CostUsd);
    }

    // ---------------------------------------------------------------- pricing / retrievedAt pass-through

    [Fact]
    public void Parse_PassesPricingAndRetrievedAtThroughToTheReport()
    {
        // SPEC P1.3 signature: "CcUsageJsonParser.Parse(string json, AgentUsagePricing pricing,
        // DateTimeOffset retrievedAt)" → the report carries them unchanged (P1.1 report rule).
        var otherInstant = new DateTimeOffset(2026, 11, 9, 18, 30, 0, TimeSpan.Zero);

        var report = CcUsageJsonParser.Parse(Root(daily: GoodDailyRow()), AgentUsagePricing.Offline, otherInstant);

        Assert.Equal(AgentUsagePricing.Offline, report.Pricing);
        Assert.Equal(otherInstant, report.RetrievedAt);
    }
}
