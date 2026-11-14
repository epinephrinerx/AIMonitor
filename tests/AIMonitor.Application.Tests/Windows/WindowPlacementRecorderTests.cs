using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.TestSupport;

namespace AIMonitor.Application.Tests.Windows;

public sealed class WindowPlacementRecorderTests
{
    [Fact]
    public async Task RecordAsync_NullArguments_ThrowsArgumentException()
    {
        var store = new BlockingSettingsStore();
        using var session = new SettingsSession(store, new AppSettings());
        var placement = new WindowPlacement(100, 100, 800, 600);
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            WindowPlacementRecorder.RecordAsync(null!, WindowGeometryManager.DashboardMode, placement, displays, now));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            WindowPlacementRecorder.RecordAsync(session, null!, placement, displays, now));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            WindowPlacementRecorder.RecordAsync(session, "   ", placement, displays, now));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            WindowPlacementRecorder.RecordAsync(session, WindowGeometryManager.DashboardMode, null!, displays, now));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            WindowPlacementRecorder.RecordAsync(session, WindowGeometryManager.DashboardMode, placement, null!, now));
    }

    [Fact]
    public async Task RecordAsync_UnderContentionWithBlockedSettingsSave_PersistsBothSettingsChangeAndPlacement()
    {
        var store = new BlockingSettingsStore();
        var initial = new AppSettings { Theme = "system", RefreshIntervalSeconds = 60 };
        using var session = new SettingsSession(store, initial);

        var placement = new WindowPlacement(200, 150, 1024, 768, false);
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var now = DateTimeOffset.UtcNow;
        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

        // 1. Block save A (a settings change)
        var settingsUpdateTask = session.UpdateAsync(s => s with
        {
            Theme = "dark",
            RefreshIntervalSeconds = 300
        });
        await store.SaveStarted.Task;

        // 2. Start WindowPlacementRecorder.RecordAsync while save A is blocked
        var recordTask = WindowPlacementRecorder.RecordAsync(session, WindowGeometryManager.DashboardMode, placement, displays, now);

        // 3. Release save A so both updates complete
        store.Release();
        await Task.WhenAll(settingsUpdateTask, recordTask);

        // 4. Assert BOTH save A's change and the placement are persisted in Current and in the store
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(300, session.Current.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, WindowGeometryManager.DashboardMode, displays, out var restored));
        Assert.Equal(200, restored.Left);
        Assert.Equal(150, restored.Top);
        Assert.Equal(1024, restored.Width);
        Assert.Equal(768, restored.Height);
        Assert.False(restored.IsMaximized);

        var lastSaved = store.SavedSettings.Last();
        Assert.Equal("dark", lastSaved.Theme);
        Assert.Equal(300, lastSaved.RefreshIntervalSeconds);
        Assert.True(WindowGeometryManager.TryRestorePlacement(lastSaved, topologyId, WindowGeometryManager.DashboardMode, displays, out var fileRestored));
        Assert.Equal(200, fileRestored.Left);
    }
}
