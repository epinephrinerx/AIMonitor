using AIMonitor.Application.Reports;
using AIMonitor.Domain;

namespace AIMonitor.Application.Tests.Reports;

public class UsageReportGeneratorTests
{
    [Fact]
    public void Build_FromSnapshots_IncludesAllProviders()
    {
        var providers = new (string, string)[]
        {
            ("claude", "Claude"),
            ("openai", "OpenAI"),
            ("gemini", "Gemini")
        };

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var claudeHistory = new UsageHistory(buckets: [
            new UsageHistoryBucket(today.AddDays(-1), new Dictionary<string, double> { ["claude-3-5"] = 1200 }),
            new UsageHistoryBucket(today, new Dictionary<string, double> { ["claude-3-5"] = 2400 })
        ]);

        var claudeSnap = new ProviderSnapshot("claude", configured: true, history: claudeHistory, account: "pro@example.com");
        var openAiSnap = new ProviderSnapshot("openai", configured: false, error: "Authentication failed");

        var snapshots = new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = claudeSnap,
            ["openai"] = openAiSnap
        };

        var report = UsageReportGenerator.Build(providers, snapshots, days: 14, metric: "Total tokens");

        Assert.Equal(3, report.Sections.Count);

        // Claude section
        var s1 = report.Sections[0];
        Assert.Equal("Claude", s1.ProviderName);
        Assert.Equal("pro@example.com", s1.Account);
        Assert.Equal(2, s1.Rows.Count);
        Assert.Equal(3600, s1.Total);
        Assert.Equal(2, s1.ActiveDays);

        // OpenAI section
        var s2 = report.Sections[1];
        Assert.Equal("OpenAI", s2.ProviderName);
        Assert.Equal("Authentication failed", s2.Note);
        Assert.Empty(s2.Rows);

        // Gemini section (unmonitored / missing snapshot)
        var s3 = report.Sections[2];
        Assert.Equal("Gemini", s3.ProviderName);
        Assert.Equal("Not monitored.", s3.Note);
    }

    [Fact]
    public void ToMarkdown_FormatsTableWithCorrectTotals()
    {
        var providers = new (string, string)[] { ("claude", "Claude") };
        var day1 = new DateOnly(2026, 10, 1);
        var history = new UsageHistory(buckets: [new UsageHistoryBucket(day1, new Dictionary<string, double> { ["sonnet"] = 5000 })]);
        var snapshots = new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = new ProviderSnapshot("claude", configured: true, history: history)
        };

        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var report = UsageReportGenerator.Build(providers, snapshots, days: 7, metric: "Total tokens", generatedAt: now);
        var md = UsageReportGenerator.ToMarkdown(report);

        Assert.Contains("# AI Usage Monitor - usage log", md);
        Assert.Contains("## Claude", md);
        Assert.Contains("| Date | Total tokens |", md);
        Assert.Contains("| 2026-10-01 | 5,000 |", md);
        Assert.Contains("| **Total** | **5,000** |", md);
    }

    [Fact]
    public void ToCsv_OutputsFlatTableWithHeaderAndEscapedValues()
    {
        var providers = new (string, string)[] { ("openai", "OpenAI / Codex") };
        var day = new DateOnly(2026, 9, 30);
        var history = new UsageHistory(buckets: [new UsageHistoryBucket(day, new Dictionary<string, double> { ["gpt-4"] = 1500 })]);
        var snapshots = new Dictionary<string, ProviderSnapshot>
        {
            ["openai"] = new ProviderSnapshot("openai", configured: true, account: "test,user", history: history)
        };

        var report = UsageReportGenerator.Build(providers, snapshots, days: 14, metric: "Total tokens");
        var csv = UsageReportGenerator.ToCsv(report);

        Assert.StartsWith("date,provider,account,source,status,metric,value", csv);
        Assert.Contains("2026-09-30", csv);
        Assert.Contains("\"OpenAI / Codex\"", csv);
        Assert.Contains("\"test,user\"", csv);
        Assert.Contains("1500", csv);
    }

    [Fact]
    public void ToHtml_EncodesEntitiesAndFormatsProperly()
    {
        var providers = new (string, string)[] { ("gemini", "Google & Gemini") };
        var snapshots = new Dictionary<string, ProviderSnapshot>
        {
            ["gemini"] = new ProviderSnapshot("gemini", configured: false, error: "Test <tag> & error")
        };

        var report = UsageReportGenerator.Build(providers, snapshots, days: 14, metric: "Total tokens");
        var html = UsageReportGenerator.ToHtml(report, dark: true);

        Assert.Contains("Google &amp; Gemini", html);
        Assert.Contains("Test &lt;tag&gt; &amp; error", html);
        Assert.Contains("font-family:Segoe UI", html);
    }

    [Fact]
    public void DefaultFilename_IncludesCurrentDate()
    {
        var when = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(7));
        var filename = UsageReportGenerator.DefaultFilename(when, "csv");

        Assert.Equal("ai-usage-log-2026-10-01.csv", filename);
    }
}
