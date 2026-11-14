using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.Presentation.Wpf;
using AIMonitor.Presentation.Wpf.Controls;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class WidgetWindowTests
{
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

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

                // Shrink height only down to window.MinHeight while keeping width unchanged at 230
                window.Height = window.MinHeight;
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

                // Force TooSmall via vm.UpdateLayout(...) with a tiny height directly
                var (h, l) = WidgetWindow.MeasureFontMetrics(window);
                vm.UpdateLayout(width: 230.0, height: 40.0, headerHeight: h, lineHeight: l);
                WpfTestHost.Realize(window);

                Assert.True(vm.IsTooSmall);
                Assert.Equal(Visibility.Visible, tooSmallText.Visibility);
                Assert.Equal(Visibility.Collapsed, itemsControl.Visibility);

                // Restore height to standard useful size
                vm.UpdateLayout(width: 230.0, height: 175.0, headerHeight: h, lineHeight: l);
                WpfTestHost.Realize(window);

                Assert.False(vm.IsTooSmall);
                Assert.Equal(Visibility.Collapsed, tooSmallText.Visibility);
                Assert.Equal(Visibility.Visible, itemsControl.Visibility);
            }
        });
    }

    [Theory]
    [InlineData(230.0, 175.0)]
    [InlineData(150.0, -1.0)]
    [InlineData(300.0, 300.0)]
    public void Geometry_MatchesModel_AtSpecifiedDimensions(double targetWidth, double targetHeight)
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude")
            {
                Status = "Connected",
                Meters =
                [
                    new MeterDisplayItem { Title = "M1", ValueText = "10%", Subtitle = "Resets in 2h" },
                    new MeterDisplayItem { Title = "M2", ValueText = "20%", Subtitle = "Resets in 2h" },
                    new MeterDisplayItem { Title = "M3", ValueText = "30%", Subtitle = "Resets in 2h" }
                ]
            };

            var vm = new WidgetViewModel([tab]);
            var session = new SettingsSession(new BlockingSettingsStore(new AppSettings()), new AppSettings());
            var window = new WidgetWindow(vm, session);

            using (WpfTestHost.ShowOffscreen(window))
            {
                double h = targetHeight > 0 ? targetHeight : window.MinHeight;
                window.Width = targetWidth;
                window.Height = h;
                WpfTestHost.Realize(window);

                var grid = (Grid)window.FindName("RootGrid")!;
                var headerRow = (RowDefinition)window.FindName("HeaderRowDefinition")!;
                var footerRow = (RowDefinition)window.FindName("FooterRowDefinition")!;
                var itemsControl = (ItemsControl)window.FindName("MetersItemsControl")!;

                // (a) the Grid inside the card is offset exactly (WidgetLayout.Margin, WidgetLayout.Margin) from window top-left
                var offset = grid.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                Assert.InRange(offset.X, WidgetLayout.Margin - 0.5, WidgetLayout.Margin + 0.5);
                Assert.InRange(offset.Y, WidgetLayout.Margin - 0.5, WidgetLayout.Margin + 0.5);

                // Find all GaugeControls
                var gauges = FindVisualChildren<GaugeControl>(itemsControl).ToList();
                Assert.Equal(vm.VisibleMeters.Count, gauges.Count);
                Assert.True(gauges.Count > 0);

                // (b) each GaugeControl.ActualWidth == vm.Cell (0.5 tolerance)
                foreach (var gauge in gauges)
                {
                    Assert.InRange(gauge.ActualWidth, vm.Cell - 0.5, vm.Cell + 0.5);

                    // (c) each gauge's bottom edge (relative to the window) <= window height - WidgetLayout.Margin - vm.FooterRowHeight + 0.5
                    var gaugePos = gauge.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                    double bottomEdge = gaugePos.Y + gauge.ActualHeight;
                    double maxAllowedBottom = window.Height - WidgetLayout.Margin - vm.FooterRowHeight + 0.5;
                    Assert.True(bottomEdge <= maxAllowedBottom);

                    // (e) GaugeControl.ActualHeight == ArcSize + 2 + CaptionLines * LineHeight
                    double expectedHeight = vm.ArcSize + 2.0 + (vm.CaptionLines * vm.LineHeight);
                    Assert.InRange(gauge.ActualHeight, expectedHeight - 0.5, expectedHeight + 0.5);
                }

                // (b continued) left-to-left distance between adjacent gauges == vm.Cell + WidgetLayout.Gap
                for (int i = 0; i < gauges.Count - 1; i++)
                {
                    var p1 = gauges[i].TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                    var p2 = gauges[i + 1].TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                    double distance = p2.X - p1.X;
                    double expectedDistance = vm.Cell + WidgetLayout.Gap;
                    Assert.InRange(distance, expectedDistance - 0.5, expectedDistance + 0.5);
                }

                // (d) header RowDefinition.ActualHeight == vm.HeaderRowHeight and footer RowDefinition.ActualHeight == vm.FooterRowHeight (0.5 tolerance)
                Assert.InRange(headerRow.ActualHeight, vm.HeaderRowHeight - 0.5, vm.HeaderRowHeight + 0.5);
                Assert.InRange(footerRow.ActualHeight, vm.FooterRowHeight - 0.5, vm.FooterRowHeight + 0.5);
            }
        });
    }

    [Fact]
    public void RealMinimumSize_GuaranteesIsTooSmallFalse_AndArcSizeAtOrAboveFloor()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude")
            {
                Status = "Connected",
                Meters =
                [
                    new MeterDisplayItem { Title = "M1", ValueText = "10%", Subtitle = "Resets in 2h" },
                    new MeterDisplayItem { Title = "M2", ValueText = "20%", Subtitle = "Resets in 2h" },
                    new MeterDisplayItem { Title = "M3", ValueText = "30%", Subtitle = "Resets in 2h" }
                ]
            };

            var vm = new WidgetViewModel([tab]);
            var session = new SettingsSession(new BlockingSettingsStore(new AppSettings()), new AppSettings());
            var window = new WidgetWindow(vm, session);

            using (WpfTestHost.ShowOffscreen(window))
            {
                window.Width = 150.0;
                window.Height = window.MinHeight;
                WpfTestHost.Realize(window);

                Assert.False(vm.IsTooSmall);
                Assert.True(vm.ArcSize >= WidgetLayout.ArcFloor);
            }
        });
    }

    [Fact]
    public void WindowChrome_AndCardBorder_AndBackground_ConfiguredCorrectly()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude");
            var vm = new WidgetViewModel([tab]);
            var window = new WidgetWindow { DataContext = vm };

            // WindowChrome config
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            Assert.NotNull(chrome);
            Assert.Equal(new Thickness(WidgetLayout.ResizeMargin), chrome.ResizeBorderThickness);
            Assert.Equal(0.0, chrome.CaptionHeight);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);

            // Card Border config: Margin 0, no Effect
            var border = Assert.IsType<Border>(window.Content);
            Assert.Equal(new Thickness(0), border.Margin);
            Assert.Null(border.Effect);

            // Window Background: SolidColorBrush with Color.A > 0 (alpha 1, not Transparent)
            var backgroundBrush = Assert.IsType<SolidColorBrush>(window.Background);
            Assert.True(backgroundBrush.Color.A > 0);
        });
    }

    [Fact]
    public void MinHeight_FollowsFontMetrics_FromRestorePlacement()
    {
        WpfTestHost.Run(() =>
        {
            var tab = new ProviderTabViewModel("claude", "Claude");
            var vm = new WidgetViewModel([tab]);
            var session = new SettingsSession(new BlockingSettingsStore(new AppSettings()), new AppSettings());
            var window = new WidgetWindow(vm, session);

            var (h, l) = WidgetWindow.MeasureFontMetrics(window);
            double minUseful = WidgetLayout.MinimumUsefulHeight(h, l);
            double expected = Math.Min(WidgetLayout.MaxEdge, Math.Max(WidgetLayout.MinHeight, minUseful));

            Assert.InRange(window.MinHeight, expected - 0.01, expected + 0.01);
            Assert.True(expected > WidgetLayout.MinHeight);
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

                // Create a NEW JsonSettingsStore and SettingsSession from the same file to verify persistence
                var reloadedStore = new JsonSettingsStore(settingsPath);
                using var reloadedSession = await SettingsSession.CreateAsync(reloadedStore);

                Assert.True(WindowGeometryManager.TryRestorePlacement(reloadedSession.Current, topologyId, WindowGeometryManager.WidgetMode, displays, out var persistedPlacement));
                Assert.Equal(265.0, persistedPlacement.Width);
                Assert.Equal(210.0, persistedPlacement.Height);
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
    public async Task ResizeEndDebounce_SavesGeometryOnTimerTick_WithoutClosingWindow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AIMonitor_WidgetDebounceTest_" + Guid.NewGuid().ToString("N"));
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
                    window.Width = 250.0;
                    WpfTestHost.Realize(window);

                    Assert.True(window.IsResizeSavePending);

                    window.OnResizeSaveTimerTick(null, EventArgs.Empty);

                    await window.WaitForPendingSaveAsync();
                    await session.FlushAsync();

                    var displays = System.Windows.Forms.Screen.AllScreens
                        .Select(s => new DisplayArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
                        .ToList();
                    var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

                    Assert.True(WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, WindowGeometryManager.WidgetMode, displays, out var savedPlacement));
                    Assert.Equal(250.0, savedPlacement.Width);
                }
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

    // WindowChrome handles band presses as non-client messages, so the OnWindowMouseDown early-return
    // is defence in depth; the helper itself is what the test covers.
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
