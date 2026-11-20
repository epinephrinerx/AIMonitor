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
    public async Task Refresh_SaysSoWhenAServiceReportsAProblem()
    {
        var (dashboard, _, _) = await BuildAsync(
            snapshots: id => id == "agy"
                ? TestData.Snapshot(id, configured: false, meters: [], error: "agy not installed")
                : TestData.Snapshot(id));

        await dashboard.RefreshAsync();

        Assert.Contains("1 service(s) reported a problem", dashboard.Status, StringComparison.Ordinal);
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
}
