using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task AttachTray_WithCachedReadings_ImmediatelyUpdatesSink()
    {
        var snapshot = new ProviderSnapshot(
            providerId: "claude",
            configured: true,
            meters: [new Meter("session", "session", "Session Quota", "", 80.0)]);
        var client = new TestQuotaClient((req, ct) => Task.FromResult(snapshot));
        var registrations = new[] { new ProviderClientRegistration("claude", client) };

        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        // Perform initial refresh without any sink attached so cached readings are stored
        await vm.RefreshAsync();

        var sink = new FakeTrayReadingsSink();
        vm.AttachTray(sink);

        // Acceptance criterion 3 & T3: AttachTray must immediately push cached readings to the new sink with content asserted
        var replayed = Assert.Single(sink.RecordedUpdates);
        var reading = Assert.Single(replayed, r => r.HasData);
        Assert.Equal("claude", reading.ProviderId);
        Assert.Equal(80.0, reading.Percentage);
    }

    [Fact]
    public async Task AttachTray_Null_DetachesSink_SoPreviousSinkReceivesNoFurtherUpdates()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        var sink = new FakeTrayReadingsSink();
        vm.AttachTray(sink);

        await vm.RefreshAsync();
        Assert.Single(sink.RecordedUpdates);

        // Detach sink
        vm.AttachTray(null);

        // Subsequent refresh must NOT invoke the detached sink
        await vm.RefreshAsync();
        Assert.Single(sink.RecordedUpdates);
    }

    [Fact]
    public async Task AttachTray_ReplacingSink_TransfersReadingsToNewSink_AndStopsUpdatingOldSink()
    {
        var snapshot = new ProviderSnapshot(
            providerId: "claude",
            configured: true,
            meters: [new Meter("session", "session", "Session Quota", "", 80.0)]);
        var client = new TestQuotaClient((req, ct) => Task.FromResult(snapshot));
        var registrations = new[] { new ProviderClientRegistration("claude", client) };

        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        var sink1 = new FakeTrayReadingsSink();
        vm.AttachTray(sink1);

        await vm.RefreshAsync();
        Assert.Single(sink1.RecordedUpdates);

        // Replace sink1 with sink2
        var sink2 = new FakeTrayReadingsSink();
        vm.AttachTray(sink2);

        // sink2 immediately gets cached readings with content asserted (T3)
        var replayed = Assert.Single(sink2.RecordedUpdates);
        var reading = Assert.Single(replayed, r => r.HasData);
        Assert.Equal("claude", reading.ProviderId);
        Assert.Equal(80.0, reading.Percentage);

        // Next refresh updates sink2 only, sink1 remains untouched
        await vm.RefreshAsync();
        Assert.Equal(2, sink2.RecordedUpdates.Count);
        Assert.Single(sink1.RecordedUpdates);
    }

    [Fact]
    public async Task AttachTray_BeforeAnyRefresh_DoesNotInvokeSinkUntilFirstRefresh()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        var sink = new FakeTrayReadingsSink();
        vm.AttachTray(sink);

        // No readings exist yet before the first refresh
        Assert.Empty(sink.RecordedUpdates);

        await vm.RefreshAsync();
        Assert.Single(sink.RecordedUpdates);
    }

    [Fact]
    public async Task RefreshAsync_WhenSubsequentRefreshReturnsAllErrors_PreservesCachedReadingsWithDataForReplay()
    {
        var returnError = false;
        var client = new TestQuotaClient((req, ct) =>
        {
            if (returnError)
            {
                return Task.FromResult(new ProviderSnapshot("claude", configured: false, error: "Provider error"));
            }

            return Task.FromResult(new ProviderSnapshot(
                providerId: "claude",
                configured: true,
                meters: [new Meter("session", "session", "Session Quota", "", 80.0)]));
        });

        var registrations = new[] { new ProviderClientRegistration("claude", client) };
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        // 1. Initial refresh returns real data (80%)
        await vm.RefreshAsync();

        // 2. Second refresh returns errors for all providers
        returnError = true;
        await vm.RefreshAsync();

        // 3. Attach a new sink and assert the replayed reading content equals the 80% reading (T3)
        var sink = new FakeTrayReadingsSink();
        vm.AttachTray(sink);

        var replayed = Assert.Single(sink.RecordedUpdates);
        var reading = Assert.Single(replayed, r => r.HasData);
        Assert.Equal("claude", reading.ProviderId);
        Assert.Equal(80.0, reading.Percentage);
    }

    private sealed class FakeTrayReadingsSink : ITrayReadingsSink
    {
        public List<IReadOnlyList<TrayReading>> RecordedUpdates { get; } = [];

        public void UpdateReadings(IEnumerable<TrayReading> readings)
        {
            RecordedUpdates.Add(readings.ToList());
        }
    }

    private sealed class TestQuotaClient : IProviderQuotaClient
    {
        public Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> Handler { get; set; }

        public TestQuotaClient(Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> handler)
        {
            Handler = handler;
        }

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
            => Handler(request, cancellationToken);
    }
}
