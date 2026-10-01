using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.TestSupport;

namespace AIMonitor.Application.Tests.Settings;

public sealed class SettingsSessionTests
{
    [Fact]
    public void Constructor_WithValidArguments_InitializesAndNormalizesCurrent()
    {
        var store = new MemorySettingsStore();
        var unnormalized = new AppSettings
        {
            Theme = "invalid-theme",
            RefreshIntervalSeconds = -10,
            WidgetOpacity = 0.05
        };

        using var session = new SettingsSession(store, unnormalized);

        Assert.Equal("system", session.Current.Theme);
        Assert.Equal(30, session.Current.RefreshIntervalSeconds);
        Assert.Equal(0.25, session.Current.WidgetOpacity);
    }

    [Fact]
    public async Task CreateAsync_LoadsFromStore_AndSetsCurrent()
    {
        var initial = new AppSettings { Theme = "dark", RefreshIntervalSeconds = 120 };
        var store = new MemorySettingsStore(initial);

        using var session = await SettingsSession.CreateAsync(store);

        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(120, session.Current.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task UpdateAsync_AppliesChangeFunction_NormalizesAndSaves()
    {
        var initial = new AppSettings { Theme = "system", RefreshIntervalSeconds = 60 };
        var store = new MemorySettingsStore(initial);
        using var session = new SettingsSession(store, initial);

        await session.UpdateAsync(s => s with
        {
            Theme = "light",
            RefreshIntervalSeconds = 15 // Under 30 min, normalizes to 30
        });

        Assert.Equal("light", session.Current.Theme);
        Assert.Equal(30, session.Current.RefreshIntervalSeconds);
        Assert.NotNull(store.SavedSettings);
        Assert.Equal("light", store.SavedSettings.Theme);
        Assert.Equal(30, store.SavedSettings.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task UpdateAsync_WhenStoreThrows_PreservesCurrent_AndRethrows()
    {
        var initial = new AppSettings { Theme = "system" };
        var store = new FailingSettingsStore(new IOException("Disk write failure"));
        using var session = new SettingsSession(store, initial);

        var ex = await Assert.ThrowsAsync<IOException>(
            () => session.UpdateAsync(s => s with { Theme = "dark" }));

        Assert.Equal("Disk write failure", ex.Message);
        Assert.Equal("system", session.Current.Theme);
    }

    [Fact]
    public async Task UpdateAsync_WhenChangeReturnsNull_ThrowsInvalidOperationException()
    {
        var store = new MemorySettingsStore();
        using var session = new SettingsSession(store, new AppSettings { Theme = "dark" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.UpdateAsync(_ => null!));

        Assert.Equal("dark", session.Current.Theme);
    }

    [Fact]
    public async Task UpdateAsync_WhenChangeFunctionIsNull_ThrowsArgumentNullException()
    {
        var store = new MemorySettingsStore();
        using var session = new SettingsSession(store, new AppSettings());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.UpdateAsync(null!));
    }

    [Fact]
    public async Task UpdateAsync_FiresChangedEvent_WithNormalizedSettings()
    {
        var store = new MemorySettingsStore();
        using var session = new SettingsSession(store, new AppSettings { Theme = "system" });

        AppSettings? eventPayload = null;
        session.Changed += s => eventPayload = s;

        await session.UpdateAsync(s => s with { Theme = "dark", RefreshIntervalSeconds = 10 });

        Assert.NotNull(eventPayload);
        Assert.Equal("dark", eventPayload.Theme);
        Assert.Equal(30, eventPayload.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentCalls_SerializesExecution()
    {
        var store = new SlowSettingsStore();
        using var session = new SettingsSession(store, new AppSettings
        {
            Theme = "system",
            RefreshIntervalSeconds = 60,
            StartWithWindows = false
        });

        var task1 = Task.Run(() => session.UpdateAsync(s => s with { Theme = "dark" }));
        var task2 = Task.Run(() => session.UpdateAsync(s => s with { RefreshIntervalSeconds = 300 }));
        var task3 = Task.Run(() => session.UpdateAsync(s => s with { StartWithWindows = true }));

        await Task.WhenAll(task1, task2, task3);

        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(300, session.Current.RefreshIntervalSeconds);
        Assert.True(session.Current.StartWithWindows);
    }

    [Fact]
    public async Task UpdateAsync_WhenCallsOverlapWithBlockingStore_PersistsBothFieldsInOrderOfArrival()
    {
        var store = new BlockingSettingsStore();
        using var session = new SettingsSession(store, new AppSettings
        {
            Theme = "system",
            RefreshIntervalSeconds = 60
        });

        // 1. Start first update modifying Theme
        var update1 = session.UpdateAsync(s => s with { Theme = "dark" });

        // 2. Wait until first update has entered SaveAsync
        await store.SaveStarted.Task;

        // 3. Second update is started while first update is blocked
        var update2 = session.UpdateAsync(s => s with { RefreshIntervalSeconds = 300 });

        // 4. Release store and let both finish
        store.Release();
        await Task.WhenAll(update1, update2);

        // 5. Assert both fields persisted in order of arrival; fails if gate is removed
        Assert.Equal(2, store.SavedSettings.Count);
        Assert.Equal("dark", store.SavedSettings[0].Theme);
        Assert.Equal(60, store.SavedSettings[0].RefreshIntervalSeconds);

        Assert.Equal("dark", store.SavedSettings[1].Theme);
        Assert.Equal(300, store.SavedSettings[1].RefreshIntervalSeconds);

        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(300, session.Current.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task AcceptanceCriteria4_FlushAsync_WaitsUntilInFlightSaveCompletes()
    {
        var store = new BlockingSettingsStore();
        using var session = new SettingsSession(store, new AppSettings { Theme = "system" });

        var updateTask = session.UpdateAsync(s => s with { Theme = "dark" });

        // Wait until the save has actually started
        await store.SaveStarted.Task;

        // Call FlushAsync and assert it is NOT completed while save is held
        var flushTask = session.FlushAsync();
        Assert.False(flushTask.IsCompleted);

        // Release the save
        store.Release();

        // Assert FlushAsync completes and settings were updated
        await flushTask;
        await updateTask;

        Assert.True(flushTask.IsCompletedSuccessfully);
        Assert.Equal("dark", session.Current.Theme);
    }

    [Fact]
    public async Task AcceptanceCriteria5_ImmediateExitAfterSave_FlushesQueuedPlacementRecord()
    {
        var store = new BlockingSettingsStore();
        using var session = new SettingsSession(store, new AppSettings { Theme = "system", RefreshIntervalSeconds = 60 });

        var placement = new WindowPlacement(120, 80, 800, 600, false);
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var now = DateTimeOffset.UtcNow;

        // 1. Start save A (a settings change) and wait until save has actually started
        var settingsUpdateTask = session.UpdateAsync(s => s with { Theme = "dark", RefreshIntervalSeconds = 300 });
        await store.SaveStarted.Task;

        // 2. Queue a placement record while save A is blocked (simulates window Closing saving geometry right before exit)
        var recordTask = WindowPlacementRecorder.RecordAsync(session, WindowGeometryManager.DashboardMode, placement, displays, now);

        // 3. Call FlushAsync (simulates App.OnExit flushing before shutdown)
        var flushTask = session.FlushAsync();
        Assert.False(flushTask.IsCompleted);

        // 4. Release save
        store.Release();

        // 5. Await flush task to complete
        await flushTask;
        await settingsUpdateTask;
        await recordTask;

        // 6. Assert the placement is persisted after flush returns
        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(300, session.Current.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, WindowGeometryManager.DashboardMode, displays, out var restoredPlacement));
        Assert.Equal(120, restoredPlacement.Left);

        Assert.NotEmpty(store.SavedSettings);
        var lastSaved = store.SavedSettings.Last();
        Assert.Equal("dark", lastSaved.Theme);
        Assert.Equal(300, lastSaved.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(lastSaved, topologyId, WindowGeometryManager.DashboardMode, displays, out var filePlacement));
        Assert.Equal(120, filePlacement.Left);
    }

    [Fact]
    public async Task Dispose_DisposesSemaphore_AndSubsequentCallsThrow()
    {
        var store = new MemorySettingsStore();
        var session = new SettingsSession(store, new AppSettings());
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.UpdateAsync(s => s));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.FlushAsync());
    }

    [Fact]
    public async Task UpdateAsync_WhenDisposedWhileUpdateInFlight_DoesNotThrowFromFinally()
    {
        var store = new BlockingSettingsStore();
        var session = new SettingsSession(store, new AppSettings { Theme = "system" });

        var updateTask = session.UpdateAsync(s => s with { Theme = "dark" });
        await store.SaveStarted.Task;

        // Dispose session while UpdateAsync is in-flight and holding the gate
        session.Dispose();

        // Release store so UpdateAsync proceeds to finally block
        store.Release();

        // Must complete without throwing ObjectDisposedException from finally block
        await updateTask;
    }

    private sealed class MemorySettingsStore(AppSettings? initial = null) : ISettingsStore
    {
        public bool Exists => true;
        public AppSettings? SavedSettings { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(initial ?? new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSettingsStore(Exception exceptionToThrow) : ISettingsStore
    {
        public bool Exists => true;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(exceptionToThrow);
    }

    private sealed class SlowSettingsStore : ISettingsStore
    {
        public bool Exists => true;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
        }
    }
}
