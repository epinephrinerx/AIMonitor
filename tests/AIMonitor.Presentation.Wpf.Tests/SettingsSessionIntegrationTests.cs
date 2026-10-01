using System.IO;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class SettingsSessionIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsSessionIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aimonitor-session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort temp cleanup
        }
    }

    [Fact]
    public async Task AcceptanceCriteria1_SaveSettingsThenSaveGeometry_PreservesSettingsAndUpdatesGeometry()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        using var session = await SettingsSession.CreateAsync(store);

        var registrar = new FakeStartupRegistrar();
        var vm = new SettingsViewModel(session, registrar);

        // User changes settings in dialog and saves
        vm.Theme = "dark";
        vm.RefreshIntervalSeconds = 300;
        await vm.SaveAsync();

        // MainWindow / WindowGeometryManager saves placement on closing via WindowPlacementRecorder
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
        var placement = new WindowPlacement(100, 100, 1200, 800, false);

        await WindowPlacementRecorder.RecordAsync(
            session,
            WindowGeometryManager.DashboardMode,
            placement,
            displays,
            DateTimeOffset.UtcNow);

        // In-memory verification
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(300, session.Current.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, WindowGeometryManager.DashboardMode, displays, out var restoredPlacement));
        Assert.Equal(100, restoredPlacement.Left);
        Assert.Equal(100, restoredPlacement.Top);

        // On-disk verification via fresh JsonSettingsStore load
        var freshStore = new JsonSettingsStore(settingsPath);
        var fromDisk = await freshStore.LoadAsync();

        Assert.Equal("dark", fromDisk.Theme);
        Assert.Equal(300, fromDisk.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(fromDisk, topologyId, WindowGeometryManager.DashboardMode, displays, out var diskPlacement));
        Assert.Equal(100, diskPlacement.Left);
        Assert.Equal(100, diskPlacement.Top);
    }

    [Fact]
    public async Task WindowPlacementRecorder_RecordAsync_WhenSettingsChangedAfterViewCaptured_PreservesSettingsAndSavesPlacement()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        using var session = await SettingsSession.CreateAsync(store);

        // Session created, caller captures nothing.
        // Another writer changes settings AFTER caller's view was captured:
        await session.UpdateAsync(s => s with
        {
            Theme = "dark",
            RefreshIntervalSeconds = 300
        });

        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
        var placement = new WindowPlacement(120, 140, 1024, 768, false);

        await WindowPlacementRecorder.RecordAsync(
            session,
            WindowGeometryManager.DashboardMode,
            placement,
            displays,
            DateTimeOffset.UtcNow);

        // Assert in-memory Current keeps dark/300 and contains placement
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(300, session.Current.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, WindowGeometryManager.DashboardMode, displays, out var currentPlacement));
        Assert.Equal(120, currentPlacement.Left);
        Assert.Equal(140, currentPlacement.Top);
        Assert.Equal(1024, currentPlacement.Width);
        Assert.Equal(768, currentPlacement.Height);

        // Assert disk keeps dark/300 and contains placement
        var freshStore = new JsonSettingsStore(settingsPath);
        var fromDisk = await freshStore.LoadAsync();
        Assert.Equal("dark", fromDisk.Theme);
        Assert.Equal(300, fromDisk.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(fromDisk, topologyId, WindowGeometryManager.DashboardMode, displays, out var diskPlacement));
        Assert.Equal(120, diskPlacement.Left);
        Assert.Equal(140, diskPlacement.Top);
        Assert.Equal(1024, diskPlacement.Width);
        Assert.Equal(768, diskPlacement.Height);
    }

    [Fact]
    public async Task AcceptanceCriteria2_ConcurrentUpdates_BothFieldsPersistedWithoutLoss()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        using var session = await SettingsSession.CreateAsync(store);

        var task1 = Task.Run(() => session.UpdateAsync(s => s with { Theme = "dark" }));
        var task2 = Task.Run(() => session.UpdateAsync(s => s with { RefreshIntervalSeconds = 240 }));

        await Task.WhenAll(task1, task2);

        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(240, session.Current.RefreshIntervalSeconds);

        var freshStore = new JsonSettingsStore(settingsPath);
        var fromDisk = await freshStore.LoadAsync();

        Assert.Equal("dark", fromDisk.Theme);
        Assert.Equal(240, fromDisk.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task AcceptanceCriteria3_StoreFailure_DoesNotChangeCurrent_AndThrows()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        using var session = await SettingsSession.CreateAsync(store);

        // Prepopulate with known value
        await session.UpdateAsync(s => s with { Theme = "system" });
        Assert.Equal("system", session.Current.Theme);

        // Create failing store
        var failingStore = new ThrowingStore(new IOException("Simulated disk error"));
        using var failingSession = new SettingsSession(failingStore, session.Current);

        var ex = await Assert.ThrowsAsync<IOException>(
            () => failingSession.UpdateAsync(s => s with { Theme = "dark" }));

        Assert.Equal("Simulated disk error", ex.Message);
        Assert.Equal("system", failingSession.Current.Theme);
    }

    [Fact]
    public async Task AcceptanceCriteria4_FlushAsync_ReturnsAfterAllPendingUpdatesComplete()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        using var session = await SettingsSession.CreateAsync(store);

        var updateTask = Task.Run(async () =>
        {
            await Task.Delay(30);
            await session.UpdateAsync(s => s with { Theme = "light", RefreshIntervalSeconds = 90 });
        });

        // Give background update time to start
        await Task.Delay(10);
        await session.FlushAsync();
        await updateTask;

        Assert.Equal("light", session.Current.Theme);
        Assert.Equal(90, session.Current.RefreshIntervalSeconds);

        var fromDisk = await new JsonSettingsStore(settingsPath).LoadAsync();
        Assert.Equal("light", fromDisk.Theme);
        Assert.Equal(90, fromDisk.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task AcceptanceCriteria5_ImmediateExitAfterSave_SettingsNotReverted()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        using var session = await SettingsSession.CreateAsync(store);

        // 1. Save settings
        await session.UpdateAsync(s => s with
        {
            Theme = "dark",
            RefreshIntervalSeconds = 450,
            StartWithWindows = false
        });

        // 2. Window closing saves geometry via WindowPlacementRecorder
        var displays = new[] { new DisplayArea(0, 0, 2560, 1440) };
        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
        var placement = new WindowPlacement(50, 50, 800, 600, false);
        _ = WindowPlacementRecorder.RecordAsync(
            session,
            WindowGeometryManager.WidgetMode,
            placement,
            displays,
            DateTimeOffset.UtcNow);

        // 3. App.OnExit flushes pending updates
        await session.FlushAsync();

        // 4. Next launch loads from same settings.json
        var nextAppStore = new JsonSettingsStore(settingsPath);
        var loaded = await nextAppStore.LoadAsync();

        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(450, loaded.RefreshIntervalSeconds);
        Assert.False(loaded.StartWithWindows);
        Assert.True(WindowGeometryManager.TryRestorePlacement(loaded, topologyId, WindowGeometryManager.WidgetMode, displays, out _));
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegistered() => false;
        public void Register(string executablePath, string arguments = "") { }
        public void Unregister() { }
    }

    private sealed class ThrowingStore(Exception ex) : ISettingsStore
    {
        public bool Exists => true;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(ex);
    }
}
