using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.Presentation.Wpf;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class WidgetWindowTests
{
    [Fact]
    public void Window_DefaultSizeAndMinMaxConstraints_MatchSpecifications()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude");
            var vm = new WidgetViewModel([tab]);
            var window = new WidgetWindow { DataContext = vm };

            Assert.Equal(230.0, window.Width);
            Assert.Equal(175.0, window.Height);
            Assert.Equal(150.0, window.MinWidth);
            Assert.Equal(300.0, window.MaxWidth);
            Assert.Equal(300.0, window.MaxHeight);
            Assert.True(window.MinHeight >= WidgetLayout.MinHeight);
            Assert.True(window.MinHeight <= WidgetLayout.MaxEdge);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        });
    }

    [Fact]
    public void NarrowingWindow_ReducesGaugeCount_AndWideningRestoresIt()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude")
            {
                Meters =
                [
                    new MeterDisplayItem { Title = "M1", ValueText = "10%" },
                    new MeterDisplayItem { Title = "M2", ValueText = "20%" },
                    new MeterDisplayItem { Title = "M3", ValueText = "30%" }
                ]
            };

            var vm = new WidgetViewModel([tab]);
            var window = new WidgetWindow { DataContext = vm };

            using (WpfTestHost.ShowOffscreen(window))
            {
                var itemsControl = (ItemsControl)window.FindName("MetersItemsControl")!;
                Assert.NotNull(itemsControl);

                // Initial width 300: room for 3 meters
                window.Width = 300.0;
                WpfTestHost.Realize(window);

                Assert.Equal(3, vm.VisibleMeters.Count);
                Assert.Equal(3, itemsControl.Items.Count);

                // Narrow width to minimum allowed width 150: room reduces to 2 meters
                window.Width = 150.0;
                WpfTestHost.Realize(window);

                Assert.Equal(2, vm.VisibleMeters.Count);
                Assert.Equal(2, itemsControl.Items.Count);

                // Widen back to 300: 3 meters restored
                window.Width = 300.0;
                WpfTestHost.Realize(window);

                Assert.Equal(3, vm.VisibleMeters.Count);
                Assert.Equal(3, itemsControl.Items.Count);
            }
        });
    }

    [Fact]
    public void HeightOnlyShrink_DoesNotReduceCellCount()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude")
            {
                Meters =
                [
                    new MeterDisplayItem { Title = "M1", ValueText = "10%" },
                    new MeterDisplayItem { Title = "M2", ValueText = "20%" },
                    new MeterDisplayItem { Title = "M3", ValueText = "30%" }
                ]
            };

            var vm = new WidgetViewModel([tab]);
            var window = new WidgetWindow { DataContext = vm };

            using (WpfTestHost.ShowOffscreen(window))
            {
                var itemsControl = (ItemsControl)window.FindName("MetersItemsControl")!;
                Assert.NotNull(itemsControl);

                Assert.Equal(3, vm.VisibleMeters.Count);
                Assert.Equal(3, itemsControl.Items.Count);

                // Shrink height only while keeping width unchanged at 230
                window.MinHeight = 40.0;
                window.Height = 80.0;
                WpfTestHost.Realize(window);

                // Width is unchanged (230), so gauge count must remain 3 despite tight vertical room
                Assert.Equal(3, vm.VisibleMeters.Count);
                Assert.Equal(3, itemsControl.Items.Count);
            }
        });
    }

    [Fact]
    public void IsTooSmall_TogglesTooSmallMessageAndMetersVisibility()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude")
            {
                Meters =
                [
                    new MeterDisplayItem { Title = "M1", ValueText = "10%" }
                ]
            };

            var vm = new WidgetViewModel([tab]);
            var window = new WidgetWindow { DataContext = vm };

            using (WpfTestHost.ShowOffscreen(window))
            {
                var itemsControl = (ItemsControl)window.FindName("MetersItemsControl")!;
                var tooSmallText = (TextBlock)window.FindName("TooSmallTextBlock")!;

                Assert.NotNull(itemsControl);
                Assert.NotNull(tooSmallText);

                // Initially at normal size: message is collapsed, items are visible
                Assert.False(vm.IsTooSmall);
                Assert.Equal(Visibility.Collapsed, tooSmallText.Visibility);
                Assert.Equal(Visibility.Visible, itemsControl.Visibility);

                // Make window height too small to fit meters
                window.MinHeight = 20.0;
                window.Height = 40.0;
                WpfTestHost.Realize(window);

                Assert.True(vm.IsTooSmall);
                Assert.Equal(Visibility.Visible, tooSmallText.Visibility);
                Assert.Equal(Visibility.Collapsed, itemsControl.Visibility);

                // Restore height to standard useful size
                window.Height = 175.0;
                WpfTestHost.Realize(window);

                Assert.False(vm.IsTooSmall);
                Assert.Equal(Visibility.Collapsed, tooSmallText.Visibility);
                Assert.Equal(Visibility.Visible, itemsControl.Visibility);
            }
        });
    }

    [Fact]
    public void RestoredSize_IsClamped_AboveMaximumAndBelowMinimum()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude");
            var vm = new WidgetViewModel([tab]);

            var displays = System.Windows.Forms.Screen.AllScreens
                .Select(s => new DisplayArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
                .ToList();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

            // 1. Saved placement exceeding maximum (800x900) -> clamped to 300x300
            var settingsLarge = new AppSettings
            {
                LegacyGeometry = new Dictionary<string, string>
                {
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/left"] = "text:100",
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/top"] = "text:100",
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/width"] = "text:800",
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/height"] = "text:900"
                }
            };

            var sessionLarge = new SettingsSession(new BlockingSettingsStore(settingsLarge), settingsLarge);
            var windowLarge = new WidgetWindow(vm, sessionLarge);

            Assert.Equal(300.0, windowLarge.Width);
            Assert.Equal(300.0, windowLarge.Height);

            // 2. Saved placement below minimum (50x50) -> clamped to MinWidth (150) and effective MinHeight
            var settingsSmall = new AppSettings
            {
                LegacyGeometry = new Dictionary<string, string>
                {
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/left"] = "text:100",
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/top"] = "text:100",
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/width"] = "text:50",
                    [$"{topologyId}/{WindowGeometryManager.WidgetMode}/height"] = "text:50"
                }
            };

            var sessionSmall = new SettingsSession(new BlockingSettingsStore(settingsSmall), settingsSmall);
            var windowSmall = new WidgetWindow(vm, sessionSmall);

            Assert.Equal(150.0, windowSmall.Width);
            Assert.Equal(windowSmall.MinHeight, windowSmall.Height);

            // 3. No saved placement -> defaults to 230x175
            var sessionEmpty = new SettingsSession(new BlockingSettingsStore(new AppSettings()), new AppSettings());
            var windowEmpty = new WidgetWindow(vm, sessionEmpty);

            Assert.Equal(230.0, windowEmpty.Width);
            Assert.Equal(175.0, windowEmpty.Height);
        });
    }

    [Fact]
    public async Task Size_IsSavedOnClose_WithJsonSettingsStore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AIMonitor_WidgetTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            var store = new JsonSettingsStore(settingsPath);
            using var session = await SettingsSession.CreateAsync(store);

            var tab = new ProviderTabViewModel("claude", "Claude");
            var vm = new WidgetViewModel([tab]);

            await WpfTestHost.RunAsync(async () =>
            {
                var window = new WidgetWindow(vm, session);

                using (WpfTestHost.ShowOffscreen(window))
                {
                    window.Width = 265.0;
                    window.Height = 210.0;
                    WpfTestHost.Realize(window);
                }

                // ShowOffscreen disposes and closes the window, which triggers SaveGeometry
                await window.WaitForPendingSaveAsync();
                await session.FlushAsync();

                var displays = System.Windows.Forms.Screen.AllScreens
                    .Select(s => new DisplayArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
                    .ToList();
                var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

                Assert.True(WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, WindowGeometryManager.WidgetMode, displays, out var savedPlacement));
                Assert.Equal(265.0, savedPlacement.Width);
                Assert.Equal(210.0, savedPlacement.Height);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public void MinHeight_FollowsFontMetricsHelper_WithInjectedLargerFont()
    {
        WpfTestHost.Run(() =>
        {
            var standard = WidgetWindow.MeasureFontMetrics(headerFontSize: 13.0, captionFontSize: 10.0);
            var larger = WidgetWindow.MeasureFontMetrics(headerFontSize: 26.0, captionFontSize: 20.0);

            Assert.True(larger.HeaderHeight > standard.HeaderHeight);
            Assert.True(larger.LineHeight > standard.LineHeight);

            var standardMinUseful = WidgetLayout.MinimumUsefulHeight(standard.HeaderHeight, standard.LineHeight);
            var largerMinUseful = WidgetLayout.MinimumUsefulHeight(larger.HeaderHeight, larger.LineHeight);

            Assert.True(largerMinUseful > standardMinUseful);
        });
    }

    [Theory]
    [InlineData(0, 50, true)]       // Left border
    [InlineData(6.9, 50, true)]     // Left border edge (< 7.0)
    [InlineData(50, 0, true)]       // Top border
    [InlineData(50, 6.9, true)]     // Top border edge (< 7.0)
    [InlineData(229, 50, true)]     // Right border (230 - 7 = 223 -> 229 >= 223)
    [InlineData(50, 174, true)]     // Bottom border (175 - 7 = 168 -> 174 >= 168)
    [InlineData(50, 50, false)]     // Inside body
    [InlineData(100, 100, false)]   // Inside body
    public void IsPointInResizeBand_CorrectlyIdentifiesPointsIn7pxBorder(double x, double y, bool expectedInBand)
    {
        WpfTestHost.Run(() =>
        {
            var window = new WidgetWindow { Width = 230.0, Height = 175.0 };
            var inBand = window.IsPointInResizeBand(new System.Windows.Point(x, y));
            Assert.Equal(expectedInBand, inBand);
        });
    }
}
