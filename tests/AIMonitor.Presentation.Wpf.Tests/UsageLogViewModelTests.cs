using AIMonitor.Application.Reports;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public class UsageLogViewModelTests
{
    [Fact]
    public void Constructor_NullReport_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new UsageLogViewModel(null!));
    }

    [Fact]
    public void Constructor_InitializesReportAndMarkdown()
    {
        var providers = new (string, string)[] { ("claude", "Claude") };
        var report = UsageReportGenerator.Build(providers, new Dictionary<string, Domain.ProviderSnapshot>(), days: 14, metric: "Total tokens");

        var vm = new UsageLogViewModel(report);

        Assert.Same(report, vm.Report);
        Assert.Contains("# AI Usage Monitor - usage log", vm.FormattedContent);
        Assert.Contains("## Claude", vm.FormattedContent);
        Assert.NotNull(vm.SaveAsCommand);
    }
}
