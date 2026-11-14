using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;

namespace AIMonitor.Application.Tests.Windows;

public sealed class WindowGeometryManagerTests
{
    [Fact]
    public void GenerateTopologyId_MatchesLegacyLayoutIdFormat()
    {
        var displays = new[]
        {
            new DisplayArea(0, 0, 1920, 1080, 1.0),
            new DisplayArea(1920, 0, 2560, 1440, 1.25)
        };

        var id = WindowGeometryManager.GenerateTopologyId(displays);

        Assert.Equal(11, id.Length);
        Assert.Equal('d', id[0]);
        Assert.True(id[1..].All(Uri.IsHexDigit));
    }

    [Fact]
    public void GenerateTopologyId_DifferentOrderingOfSameDisplays_ProducesSameId()
    {
        var d1 = new DisplayArea(0, 0, 1920, 1080, 1.0);
        var d2 = new DisplayArea(1920, 0, 2560, 1440, 1.25);

        var id1 = WindowGeometryManager.GenerateTopologyId([d1, d2]);
        var id2 = WindowGeometryManager.GenerateTopologyId([d2, d1]);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void FitIntoVisibleArea_WindowAlreadyFullyVisible_RemainsUnchanged()
    {
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var requested = new WindowPlacement(100, 100, 1120, 820);
        var fallback = new WindowPlacement(50, 50, 1120, 820);

        var fitted = WindowGeometryManager.FitIntoVisibleArea(requested, displays, fallback);

        Assert.Equal(100, fitted.Left);
        Assert.Equal(100, fitted.Top);
        Assert.Equal(1120, fitted.Width);
        Assert.Equal(820, fitted.Height);
    }

    [Fact]
    public void FitIntoVisibleArea_WindowCompletelyOffScreen_BringsIntoVisibleArea()
    {
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        // Window located completely to the right of the screen (e.g. disconnected secondary monitor)
        var requested = new WindowPlacement(3000, 2000, 1120, 820);
        var fallback = new WindowPlacement(0, 0, 1120, 820);

        var fitted = WindowGeometryManager.FitIntoVisibleArea(requested, displays, fallback);

        Assert.True(fitted.Left >= 0 && fitted.Left + fitted.Width <= 1920);
        Assert.True(fitted.Top >= 0 && fitted.Top + fitted.Height <= 1080);
    }

    [Fact]
    public void FitIntoVisibleArea_InvalidPlacement_ReturnsFallback()
    {
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var requested = new WindowPlacement(double.NaN, 0, -500, 820);
        var fallback = new WindowPlacement(100, 100, 1120, 820);

        var fitted = WindowGeometryManager.FitIntoVisibleArea(requested, displays, fallback);

        Assert.Equal(fallback, fitted);
    }

    [Fact]
    public void SavePlacement_StoresPlacementAndLimitsTo8Layouts()
    {
        var settings = new AppSettings();
        var baseTime = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        // Save 10 distinct layouts (10 > MaxRetainedLayouts 8)
        for (var i = 1; i <= 10; i++)
        {
            var layoutId = $"d{i:x10}";
            var placement = new WindowPlacement(100 + i * 10, 100 + i * 10, 1120, 820);
            var timestamp = baseTime.AddHours(i);

            settings = WindowGeometryManager.SavePlacement(
                settings,
                layoutId,
                WindowGeometryManager.DashboardMode,
                placement,
                timestamp);
        }

        // Verify only 8 layout IDs exist
        var distinctLayoutIds = settings.LegacyGeometry.Keys
            .Select(k => k.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(8, distinctLayoutIds.Length);

        // Oldest layouts (d0000000001, d0000000002) must have been evicted
        Assert.DoesNotContain("d0000000001", distinctLayoutIds);
        Assert.DoesNotContain("d0000000002", distinctLayoutIds);

        // Most recent layout (d000000000a) must exist
        Assert.Contains("d000000000a", distinctLayoutIds);
    }

    [Fact]
    public void TryRestorePlacement_WhenSaved_RestoresCorrectly()
    {
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var settings = new AppSettings();
        var now = DateTimeOffset.UtcNow;
        var layoutId = "d0123456789";

        settings = WindowGeometryManager.SavePlacement(
            settings,
            layoutId,
            WindowGeometryManager.DashboardMode,
            new WindowPlacement(250, 150, 1000, 700, false),
            now);

        var success = WindowGeometryManager.TryRestorePlacement(
            settings,
            layoutId,
            WindowGeometryManager.DashboardMode,
            displays,
            out var restored);

        Assert.True(success);
        Assert.Equal(250, restored.Left);
        Assert.Equal(150, restored.Top);
        Assert.Equal(1000, restored.Width);
        Assert.Equal(700, restored.Height);
        Assert.False(restored.IsMaximized);
    }

    [Fact]
    public void TryRestorePlacement_WhenLayoutNotFound_ReturnsFalse()
    {
        var displays = new[] { new DisplayArea(0, 0, 1920, 1080) };
        var settings = new AppSettings();

        var success = WindowGeometryManager.TryRestorePlacement(
            settings,
            "d9999999999",
            WindowGeometryManager.DashboardMode,
            displays,
            out var restored);

        Assert.False(success);
        Assert.Null(restored);
    }
}
