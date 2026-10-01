using AIMonitor.Application.Settings;

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
    public async Task FlushAsync_WaitsForInFlightUpdates()
    {
        var store = new ControllableSettingsStore();
        using var session = new SettingsSession(store, new AppSettings { Theme = "system" });

        var updateTask = session.UpdateAsync(s => s with { Theme = "dark" });

        // Wait until store has entered SaveAsync
        await store.SaveEntered.Task;

        var flushTask = session.FlushAsync();
        Assert.False(flushTask.IsCompleted);

        // Allow SaveAsync to complete
        store.SaveCompletion.TrySetResult();

        await Task.WhenAll(updateTask, flushTask);

        Assert.Equal("dark", session.Current.Theme);
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
        var store = new ControllableSettingsStore();
        var session = new SettingsSession(store, new AppSettings { Theme = "system" });

        var updateTask = session.UpdateAsync(s => s with { Theme = "dark" });
        await store.SaveEntered.Task;

        // Dispose session while UpdateAsync is in-flight and holding the gate
        session.Dispose();

        // Release store so UpdateAsync proceeds to finally block
        store.SaveCompletion.TrySetResult();

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

    private sealed class ControllableSettingsStore : ISettingsStore
    {
        public bool Exists => true;
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await SaveCompletion.Task.WaitAsync(cancellationToken);
        }
    }
}
