using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;

namespace AIMonitor.Application.Tests.Windows;

public sealed class WindowPlacementRecorderTests
{
    [Fact]
    public async Task RecordAsync_NullArguments_ThrowsArgumentException()
    {
        var store = new MemorySettingsStore();
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
    public async Task RecordAsync_ValidArguments_UpdatesSessionCurrentWithPlacement()
    {
        var store = new MemorySettingsStore();
        using var session = new SettingsSession(store, new AppSettings());
        var placement = new WindowPlacement(200, 150, 1024, 768, false);
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var now = DateTimeOffset.UtcNow;

        await WindowPlacementRecorder.RecordAsync(session, WindowGeometryManager.DashboardMode, placement, displays, now);

        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
        Assert.True(WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, WindowGeometryManager.DashboardMode, displays, out var restored));
        Assert.Equal(200, restored.Left);
        Assert.Equal(150, restored.Top);
        Assert.Equal(1024, restored.Width);
        Assert.Equal(768, restored.Height);
        Assert.False(restored.IsMaximized);
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public bool Exists => true;
        public AppSettings? SavedSettings { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }
}
