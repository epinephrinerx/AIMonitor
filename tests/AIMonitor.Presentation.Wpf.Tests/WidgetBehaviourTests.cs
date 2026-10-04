using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>Widget rotation, paging, pinning and caption lines per 1.3.3 <c>CompactView</c> / <c>main_window.py</c>.</summary>
public sealed class WidgetBehaviourTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static ProviderTabViewModel Tab(string id, string name, bool configured, double? percent = 40)
    {
        var tab = new ProviderTabViewModel(id, name);
        var meters = configured && percent is not null
            ? new[] { new Meter("session", "session", "Session", "5-hour window", percent, resetsAt: Now.AddMinutes(125)) }
            : [];
        tab.UpdateFromSnapshot(new ProviderSnapshot(id, configured: configured, meters: meters, fetchedAt: Now), Now);
        return tab;
    }

    [Fact]
    public void Rotation_SkipsUnconfiguredServices_AndHidesChevronsWhenNothingToPageTo()
    {
        var claude = Tab("claude", "Claude", true);
        var openai = Tab("openai", "OpenAI", false);
        var gemini = Tab("gemini", "Gemini", false);
        var vm = new WidgetViewModel([claude, openai, gemini]);

        Assert.False(vm.CanPage);
        vm.NextProvider();
        Assert.Same(claude, vm.CurrentProvider);
    }

    [Fact]
    public void Paging_StepsAmongConfiguredServices()
    {
        var claude = Tab("claude", "Claude", true);
        var openai = Tab("openai", "OpenAI", false);
        var gemini = Tab("gemini", "Gemini", true);
        var vm = new WidgetViewModel([claude, openai, gemini]);

        Assert.True(vm.CanPage);
        vm.NextProvider();
        Assert.Same(gemini, vm.CurrentProvider);
        vm.NextProvider();
        Assert.Same(claude, vm.CurrentProvider);
        vm.PreviousProvider();
        Assert.Same(gemini, vm.CurrentProvider);
    }

    [Fact]
    public void ShowProvider_PinsAndStopsRotation_ShowAllRestoresIt()
    {
        var claude = Tab("claude", "Claude", true);
        var gemini = Tab("gemini", "Gemini", true);
        var vm = new WidgetViewModel([claude, gemini]);
        vm.SetActive(true);
        Assert.True(vm.IsRotationTimerRunning);

        vm.ShowProvider("gemini");
        Assert.Same(gemini, vm.CurrentProvider);
        Assert.True(vm.IsPinned);
        Assert.False(vm.RotationEnabled);
        Assert.False(vm.IsRotationTimerRunning);

        vm.ShowAllProviders();
        Assert.False(vm.IsPinned);
        Assert.True(vm.IsRotatingAll);
        Assert.True(vm.IsRotationTimerRunning);
        vm.SetActive(false);
        Assert.False(vm.IsRotationTimerRunning);
    }

    [Fact]
    public void Paging_WhileRotationIsOff_PinsTheTarget()
    {
        var claude = Tab("claude", "Claude", true);
        var gemini = Tab("gemini", "Gemini", true);
        var vm = new WidgetViewModel([claude, gemini]);
        vm.ApplySettings(new AppSettings { WidgetRotationEnabled = false });
        vm.ActiveProviderSource = () => "claude";

        Assert.Same(claude, vm.CurrentProvider);
        vm.NextProvider();
        Assert.Same(gemini, vm.CurrentProvider);
        Assert.Equal("gemini", vm.PinnedProviderId);
    }

    [Fact]
    public void RotationOff_FollowsTheDashboardTab()
    {
        var claude = Tab("claude", "Claude", true);
        var gemini = Tab("gemini", "Gemini", true);
        var vm = new WidgetViewModel([claude, gemini]);
        vm.ApplySettings(new AppSettings { WidgetRotationEnabled = false });
        vm.ActiveProviderSource = () => "gemini";

        Assert.Same(gemini, vm.CurrentProvider);
    }

    [Fact]
    public void Footer_SaysHowOldTheReadingIs()
    {
        Assert.Equal("", WidgetViewModel.FormatStatus(null, Now));
        Assert.Equal("updated just now", WidgetViewModel.FormatStatus(Now.AddSeconds(-3), Now));
        Assert.Equal("updated 5m ago", WidgetViewModel.FormatStatus(Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void LeadLines_ShowResetCountdown_AndSeverityWordOnceNotNormal()
    {
        var tab = new ProviderTabViewModel("claude", "Claude");
        tab.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, fetchedAt: Now,
            meters: [new Meter("session", "session", "Session", "5-hour window", 92, resetsAt: Now.AddMinutes(125))]), Now);
        var vm = new WidgetViewModel([tab]);
        vm.UpdateLayout(300, 300, 16, 12);

        Assert.True(vm.ShowLeadHead);
        Assert.Contains("Session", vm.LeadHeadText);
        Assert.NotEqual("Session", vm.LeadHeadText); // glyph and word are added at 92%
        Assert.True(vm.ShowResetLine);
        Assert.StartsWith("resets in 2h 5m", vm.ResetLineText);
    }

    [Fact]
    public void NormalLead_ShowsPlainTitle()
    {
        var vm = new WidgetViewModel([Tab("claude", "Claude", true, 12)]);
        vm.UpdateLayout(300, 300, 16, 12);
        Assert.Equal("Session", vm.LeadHeadText);
    }

    [Fact]
    public void EmptyMessage_ExplainsWhyThereIsNothingToDraw()
    {
        var waiting = new WidgetViewModel([new ProviderTabViewModel("claude", "Claude")]);
        Assert.Equal("Waiting for data…", waiting.EmptyMessage);

        var notConfigured = new WidgetViewModel([Tab("openai", "OpenAI", false)]);
        Assert.Equal("OpenAI is not configured yet.", notConfigured.EmptyMessage);

        var failing = new ProviderTabViewModel("claude", "Claude");
        failing.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, error: "token expired"), Now);
        Assert.Equal("token expired", new WidgetViewModel([failing]).EmptyMessage);
    }

    [Fact]
    public void MenuActions_RaiseTheirEvents()
    {
        var vm = new WidgetViewModel([Tab("claude", "Claude", true)]);
        var seen = new List<string>();
        vm.RequestExpand += () => seen.Add("expand");
        vm.RequestRefresh += () => seen.Add("refresh");
        vm.RequestQuit += () => seen.Add("quit");

        vm.Expand();
        vm.Refresh();
        vm.Quit();

        Assert.Equal(["expand", "refresh", "quit"], seen);
    }
}
