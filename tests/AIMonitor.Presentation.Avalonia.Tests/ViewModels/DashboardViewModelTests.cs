using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

public sealed class DashboardViewModelTests
{
    private sealed class StubClient(Func<ProviderSnapshotRequest, ProviderSnapshot> snapshot) : IProviderQuotaClient
    {
        public List<ProviderSnapshotRequest> Requests { get; } = [];

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(snapshot(request));
        }
    }

    private static async Task<(DashboardViewModel Dashboard, SettingsSession Settings, Dictionary<string, StubClient> Clients)>
        BuildAsync(AppSettings? settings = null, Func<string, ProviderSnapshot>? snapshots = null)
    {
        var store = new InMemorySettingsStore(settings);
        var session = await SettingsSession.CreateAsync(store);
        var clients = new[] { "claude", "openai", "agy" }.ToDictionary(
            id => id,
            id => new StubClient(_ => snapshots?.Invoke(id) ?? TestData.Snapshot(id)),
            StringComparer.Ordinal);

        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
            [.. clients.Select(entry => new ProviderClientRegistration(entry.Key, entry.Value))]));

        return (new DashboardViewModel(coordinator, session), session, clients);
    }

    [Fact]
    public async Task ShowsTheThreeServicesTheAppMonitors()
    {
        var (dashboard, _, _) = await BuildAsync();

        Assert.Equal(["claude", "openai", "agy"], dashboard.Providers.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task StartsOnTheServiceTheUserWasLastLookingAt()
    {
        var (dashboard, _, _) = await BuildAsync(new AppSettings { ActiveProvider = "agy" });

        Assert.Equal("agy", dashboard.SelectedProvider.ProviderId);
    }

    [Fact]
    public async Task WithAnUnknownSavedService_FallsBackToTheFirstRatherThanShowingNothing()
    {
        var (dashboard, _, _) = await BuildAsync(new AppSettings { ActiveProvider = "nothing-of-ours" });

        Assert.Equal("claude", dashboard.SelectedProvider.ProviderId);
    }

    [Fact]
    public async Task Refresh_FillsEveryServiceFromItsOwnSnapshot()
    {
        var (dashboard, _, _) = await BuildAsync(
            snapshots: id => TestData.Snapshot(id, meters: [TestData.Meter(percent: id == "claude" ? 10 : 20)]));

        await dashboard.RefreshAsync();

        Assert.Equal(10, dashboard.Providers[0].Meters[0].Percent);
        Assert.Equal(20, dashboard.Providers[1].Meters[0].Percent);
    }

    [Fact]
    public async Task Refresh_SkipsAServiceTheUserSwitchedMonitoringOffFor()
    {
        var settings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new(Enabled: false),
            },
        };
        var (dashboard, _, clients) = await BuildAsync(settings);

        await dashboard.RefreshAsync();

        Assert.Empty(clients["openai"].Requests);
        Assert.NotEmpty(clients["claude"].Requests);
    }

    [Fact]
    public async Task Refresh_ComposesTheFooterTheWayWindowsDoes()
    {
        var (dashboard, _, _) = await BuildAsync(new AppSettings { RefreshIntervalSeconds = 180 });

        await dashboard.RefreshAsync();
        dashboard.RefreshStatusText(DateTimeOffset.UtcNow.AddMinutes(5), 85);

        Assert.StartsWith("Updated 5m ago (", dashboard.Status, StringComparison.Ordinal);
        Assert.Contains("auto-refresh every 3 minutes · default", dashboard.Status, StringComparison.Ordinal);
        Assert.EndsWith("85 MB resident", dashboard.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProblemWithOneService_IsShownOnItsTab()
    {
        var (dashboard, _, _) = await BuildAsync(
            snapshots: id => id == "agy"
                ? TestData.Snapshot(id, configured: false, meters: [], error: "agy not installed")
                : TestData.Snapshot(id));

        await dashboard.RefreshAsync();

        Assert.Equal("agy not installed", dashboard.Providers[2].ErrorMessage);
        Assert.StartsWith("Updated", dashboard.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_WithEveryServiceHealthy_JustSaysWhenItUpdated()
    {
        var (dashboard, _, _) = await BuildAsync();

        await dashboard.RefreshAsync();

        Assert.StartsWith("Updated ", dashboard.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("problem", dashboard.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_RaisesRefreshed_SoTheMenuBarAndWidgetRedrawFromTheSameReadings()
    {
        var (dashboard, _, _) = await BuildAsync();
        var raised = 0;
        dashboard.Refreshed += () => raised++;

        await dashboard.RefreshAsync();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Refresh_RaisesRefreshedEvenWhenEverythingFailed()
    {
        var (dashboard, _, _) = await BuildAsync(
            snapshots: id => TestData.Snapshot(id, configured: false, meters: [], error: "offline"));
        var raised = 0;
        dashboard.Refreshed += () => raised++;

        await dashboard.RefreshAsync();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ChoosingAService_RemembersItForNextTime()
    {
        var (dashboard, session, _) = await BuildAsync();

        dashboard.SelectedProvider = dashboard.Providers[2];
        await session.FlushAsync();

        Assert.Equal("agy", session.Current.ActiveProvider);
    }

    [Fact]
    public async Task RefreshInterval_ComesFromSettings()
    {
        var (dashboard, _, _) = await BuildAsync(new AppSettings { RefreshIntervalSeconds = 300 });

        Assert.Equal(TimeSpan.FromMinutes(5), dashboard.RefreshInterval);
    }

    [Fact]
    public async Task RefreshInterval_OfZeroMeansManualOnly()
    {
        var (dashboard, _, _) = await BuildAsync(new AppSettings { RefreshIntervalSeconds = 0 });

        Assert.Equal(TimeSpan.Zero, dashboard.RefreshInterval);
    }

    [Fact]
    public async Task ASkippedServiceKeepsItsPreviousReadings_RatherThanBeingWipedAsAFailure()
    {
        var settings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new(Enabled: false),
            },
        };
        var (dashboard, _, _) = await BuildAsync(settings);

        await dashboard.RefreshAsync();
        var openAi = dashboard.Providers[1];

        Assert.False(openAi.Configured);
        Assert.False(openAi.HasError);
    }

    // ---- Parity with the Windows dashboard header and refresh (PAR-003, PAR-015) ----

    [Fact]
    public async Task TheHeaderSelectors_StartOnTheSavedChoices()
    {
        var (dashboard, _, _) = await BuildAsync(new AppSettings
        {
            ChartMetric = "Output tokens",
            ChartRangeDays = 30,
            RefreshIntervalSeconds = 0,
        });

        Assert.Equal("Output tokens", dashboard.SelectedMetric.Value);
        Assert.Equal(30, dashboard.SelectedRange.Value);
        Assert.Equal("Manual only", dashboard.SelectedInterval.Label);
    }

    [Fact]
    public async Task ChoosingAMetric_SavesIt_AndAsksTheServicesAgain()
    {
        var (dashboard, session, clients) = await BuildAsync();

        dashboard.SelectedMetric = dashboard.MetricOptions.Single(option => option.Value == "Equivalent value");
        await session.FlushAsync();
        for (var i = 0; i < 50 && clients["claude"].Requests.Count == 0; i++) await Task.Delay(10);

        Assert.Equal("Equivalent value", session.Current.ChartMetric);
        Assert.Equal("Equivalent value", clients["claude"].Requests[^1].Metric);
    }

    [Fact]
    public async Task ChoosingAnInterval_SavesIt()
    {
        var (dashboard, session, _) = await BuildAsync();

        dashboard.SelectedInterval = dashboard.IntervalOptions.Single(option => option.Value == 600);
        await session.FlushAsync();

        Assert.Equal(600, session.Current.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task ASettingSavedElsewhere_MovesTheHeaderSelector()
    {
        var (dashboard, session, _) = await BuildAsync();

        await session.UpdateAsync(s => s with { ChartRangeDays = 90 });

        Assert.Equal(90, dashboard.SelectedRange.Value);
    }

    [Fact]
    public async Task TheRefreshButton_SaysWhenItIsWorking()
    {
        var release = new TaskCompletionSource();
        var session = await SettingsSession.CreateAsync(new InMemorySettingsStore());
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
            [new ProviderClientRegistration("claude", new GatedClient(() => release.Task))]));
        var dashboard = new DashboardViewModel(coordinator, session);

        Assert.Equal("Refresh", dashboard.RefreshButtonText);
        var running = dashboard.RefreshAsync();
        Assert.Equal("Refreshing…", dashboard.RefreshButtonText);
        release.SetResult();
        await running;
        Assert.Equal("Refresh", dashboard.RefreshButtonText);
    }

    [Fact]
    public async Task ARefreshAskedForDuringOne_StillRunsAfterIt()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var store = new InMemorySettingsStore();
        var session = await SettingsSession.CreateAsync(store);
        var slow = new GatedClient(async () =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
        });
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
            [new ProviderClientRegistration("claude", slow)]));
        var dashboard = new DashboardViewModel(coordinator, session);

        var first = dashboard.RefreshAsync();
        var second = dashboard.RefreshAsync();
        release.SetResult();
        await first;
        await second;

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReDetectingOneService_LeavesTheOthersAsTheyWere()
    {
        var round = 0;
        var (dashboard, _, clients) = await BuildAsync(
            snapshots: id => TestData.Snapshot(id, meters: [TestData.Meter(percent: round == 0 ? 10 : 50)]));
        await dashboard.RefreshAsync();
        round = 1;

        await dashboard.RefreshAsync(manual: true, onlyProviderId: "openai");

        Assert.Equal(10, dashboard.Providers[0].Meters[0].Percent);
        Assert.Equal(50, dashboard.Providers[1].Meters[0].Percent);
        Assert.True(dashboard.LatestSnapshots.ContainsKey("claude"));
    }

    [Fact]
    public async Task TheAppearanceCheckMark_FollowsTheSavedTheme()
    {
        var (dashboard, session, _) = await BuildAsync(new AppSettings { Theme = "dark" });

        Assert.True(dashboard.IsDarkTheme);
        await session.UpdateAsync(s => s with { Theme = "system" });
        Assert.True(dashboard.IsSystemTheme);
        Assert.False(dashboard.IsDarkTheme);
    }

    private sealed class GatedClient(Func<Task> before) : IProviderQuotaClient
    {
        public async Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
        {
            await before();
            return TestData.Snapshot("claude");
        }
    }
}
