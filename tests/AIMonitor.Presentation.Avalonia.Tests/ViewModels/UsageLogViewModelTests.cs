using AIMonitor.Application.Reports;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

public sealed class UsageLogViewModelTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private static UsageLogViewModel Log()
    {
        var report = UsageReportGenerator.Build(
            [("claude", "Claude"), ("openai", "OpenAI / Codex")],
            new Dictionary<string, ProviderSnapshot> { ["claude"] = TestData.Snapshot("claude") },
            days: 14,
            metric: "Total tokens",
            generatedAt: GeneratedAt);
        return new UsageLogViewModel(report);
    }

    [Fact]
    public void OpensShowingTheReportAsReadableText()
    {
        Assert.NotEmpty(Log().Content);
    }

    [Fact]
    public void SuggestsAFileNameWithTheDateInIt()
    {
        Assert.Equal("ai-usage-log-2026-10-31.csv", Log().SuggestedFileName);
    }

    [Theory]
    [InlineData("log.csv")]
    [InlineData("LOG.CSV")]
    public void SavingAsCsvProducesASpreadsheet_NotMarkdownUnderACsvName(string fileName)
    {
        var rendered = Log().Render(fileName);

        Assert.Equal(UsageReportGenerator.ToCsv(Log().Report), rendered);
    }

    [Theory]
    [InlineData("log.html")]
    [InlineData("log.htm")]
    public void SavingAsHtmlProducesAWebPage(string fileName)
    {
        Assert.StartsWith("<", Log().Render(fileName).TrimStart(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("log.md")]
    [InlineData("log")]
    [InlineData("log.txt")]
    public void AnythingElseIsSavedAsTheTextThatIsOnScreen(string fileName)
    {
        var log = Log();

        Assert.Equal(log.Content, log.Render(fileName));
    }

    [Fact]
    public void ASuccessfulSaveIsReportedByName()
    {
        var log = Log();

        log.ReportSaved("/Users/someone/Documents/ai-usage-log-2026-10-31.csv");

        Assert.Equal("Saved to ai-usage-log-2026-10-31.csv", log.Message);
        Assert.True(log.HasMessage);
    }

    [Fact]
    public void AFailedSaveSaysWhy()
    {
        var log = Log();

        log.ReportSaveFailed("the disk is full");

        Assert.Contains("the disk is full", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAs_AsksTheWindowToPickAFile_RatherThanPickingOneItself()
    {
        var log = Log();
        var asked = 0;
        log.SaveRequested += () => asked++;

        log.SaveAsCommand.Execute(null);

        Assert.Equal(1, asked);
    }
}
