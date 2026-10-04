using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public class ProviderTabViewModelTests
{
    [Fact]
    public void Constructor_InitializesProperties()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");

        Assert.Equal("claude", vm.ProviderId);
        Assert.Equal("Claude", vm.DisplayName);
        Assert.Equal("Checking...", vm.Status);
        Assert.Empty(vm.Meters);
        Assert.Empty(vm.ChartBuckets);
    }

    [Fact]
    public void UpdateFromSnapshot_NullSnapshot_ThrowsArgumentNullException()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        Assert.Throws<ArgumentNullException>(() => vm.UpdateFromSnapshot(null!));
    }

    [Fact]
    public void UpdateFromSnapshot_WithDetectionState_UpdatesStatusAndDetail()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var detection = new DetectionInfo("openai", DetectionState.Connected, "env", "API Key", account: "user@example.com", hint: "Direct key detected");
        var snapshot = new ProviderSnapshot("openai", configured: true, detection: detection, account: "user@example.com");

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal("Connected", vm.Status);
        Assert.Equal("Direct key detected", vm.StatusDetail);
        Assert.Equal("Account: user@example.com", vm.AccountInfo);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public void UpdateFromSnapshot_WithError_UpdatesErrorProperties()
    {
        var vm = new ProviderTabViewModel("gemini", "Gemini");
        var snapshot = new ProviderSnapshot("gemini", configured: false, error: "Authentication failed 401");

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal("Error", vm.Status);
        Assert.Equal("Authentication failed 401", vm.ErrorMessage);
    }

    [Fact]
    public void UpdateFromSnapshot_WithMeters_PopulatesMeterDisplayItems()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var meter = new Meter("session", "session", "5-hour session", "Resets soon", percent: 65.4, serverSeverity: Severity.Normal);
        var snapshot = new ProviderSnapshot("claude", configured: true, meters: [meter]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.Single(vm.Meters);
        var item = vm.Meters[0];
        Assert.Equal("5-hour session", item.Title);
        Assert.Equal(65.4, item.Value);
        Assert.Equal("65%", item.ValueText);
        Assert.Equal(Severity.Normal, item.Severity);
    }

    [Fact]
    public void UpdateFromSnapshot_WithHistoryBuckets_PopulatesChartData()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var bucket = new UsageHistoryBucket(today, new Dictionary<string, double> { ["gpt-4"] = 2500 });
        var history = new UsageHistory(buckets: [bucket]);
        var snapshot = new ProviderSnapshot("openai", configured: true, history: history);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.HasHistory);
        var day = Assert.Single(vm.ChartBuckets);
        Assert.Equal(today, day.Day);
        Assert.Equal(2500, day.Total);
        Assert.Equal("Usage per day · last 14 days", vm.ChartTitle);
    }

    [Fact]
    public void UpdateFromSnapshot_WithStats_FormatsStatsSummary()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var stat1 = new Stat("Tokens Today", "15,200");
        var stat2 = new Stat("Cost", "$0.45");
        var snapshot = new ProviderSnapshot("claude", configured: true, stats: [stat1, stat2]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal("Tokens Today: 15,200  |  Cost: $0.45", vm.StatsSummary);
    }
}
