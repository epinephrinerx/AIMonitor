using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task AttachTray_WithCachedReadings_ImmediatelyUpdatesSink()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        // Perform initial refresh without any sink attached so cached readings are stored
        await vm.RefreshAsync();

        var sink = new FakeTrayReadingsSink();
        vm.AttachTray(sink);

        // Acceptance criterion 3: AttachTray must immediately push cached readings to the new sink
        Assert.Single(sink.RecordedUpdates);
        Assert.NotEmpty(sink.RecordedUpdates[0]);
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
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
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

        // sink2 immediately gets cached readings
        Assert.Single(sink2.RecordedUpdates);

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

    private sealed class FakeTrayReadingsSink : ITrayReadingsSink
    {
        public List<IReadOnlyList<TrayReading>> RecordedUpdates { get; } = [];

        public void UpdateReadings(IEnumerable<TrayReading> readings)
        {
            RecordedUpdates.Add(readings.ToList());
        }
    }
}
