using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public class WidgetViewModelTests
{
    [Fact]
    public void Constructor_NullProviders_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new WidgetViewModel(null!));
    }

    [Fact]
    public void Constructor_InitializesWithFirstProvider()
    {
        var tab1 = new ProviderTabViewModel("claude", "Claude");
        var tab2 = new ProviderTabViewModel("openai", "OpenAI");

        var vm = new WidgetViewModel([tab1, tab2]);

        Assert.Same(tab1, vm.CurrentProvider);
        Assert.False(vm.IsPinned);
        Assert.True(vm.AlwaysOnTop);
        Assert.True(vm.Opacity > 0);
    }

    [Fact]
    public void NextProvider_CyclesForwardWithWrapAround()
    {
        var tab1 = new ProviderTabViewModel("claude", "Claude");
        var tab2 = new ProviderTabViewModel("openai", "OpenAI");
        var tab3 = new ProviderTabViewModel("gemini", "Gemini");

        var vm = new WidgetViewModel([tab1, tab2, tab3]);

        Assert.Same(tab1, vm.CurrentProvider);

        vm.NextProvider();
        Assert.Same(tab2, vm.CurrentProvider);

        vm.NextProvider();
        Assert.Same(tab3, vm.CurrentProvider);

        vm.NextProvider();
        Assert.Same(tab1, vm.CurrentProvider);
    }

    [Fact]
    public void PreviousProvider_CyclesBackwardWithWrapAround()
    {
        var tab1 = new ProviderTabViewModel("claude", "Claude");
        var tab2 = new ProviderTabViewModel("openai", "OpenAI");
        var tab3 = new ProviderTabViewModel("gemini", "Gemini");

        var vm = new WidgetViewModel([tab1, tab2, tab3]);

        Assert.Same(tab1, vm.CurrentProvider);

        vm.PreviousProvider();
        Assert.Same(tab3, vm.CurrentProvider);

        vm.PreviousProvider();
        Assert.Same(tab2, vm.CurrentProvider);
    }

    [Fact]
    public void TogglePinCommand_TogglesPinState()
    {
        var tab = new ProviderTabViewModel("claude", "Claude");
        var vm = new WidgetViewModel([tab]);

        Assert.False(vm.IsPinned);

        vm.TogglePinCommand.Execute(null);
        Assert.True(vm.IsPinned);

        vm.TogglePinCommand.Execute(null);
        Assert.False(vm.IsPinned);
    }

    [Fact]
    public void ApplySettings_NullSettings_ThrowsArgumentNullException()
    {
        var tab = new ProviderTabViewModel("claude", "Claude");
        var vm = new WidgetViewModel([tab]);

        Assert.Throws<ArgumentNullException>(() => vm.ApplySettings(null!));
    }

    [Fact]
    public void ApplySettings_UpdatesOpacityAndAlwaysOnTop_AndFiresPropertyChanged()
    {
        var tab = new ProviderTabViewModel("claude", "Claude");
        var vm = new WidgetViewModel([tab]);

        var changedProps = new List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is not null)
            {
                changedProps.Add(e.PropertyName);
            }
        };

        var settings = new AppSettings
        {
            WidgetOpacity = 0.65,
            WidgetAlwaysOnTop = false
        };

        vm.ApplySettings(settings);

        Assert.Equal(0.65, vm.Opacity);
        Assert.False(vm.AlwaysOnTop);
        Assert.Contains(nameof(WidgetViewModel.Opacity), changedProps);
        Assert.Contains(nameof(WidgetViewModel.AlwaysOnTop), changedProps);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    public void ApplySettings_BoundaryValues_Applies025And10(double boundaryOpacity)
    {
        var tab = new ProviderTabViewModel("claude", "Claude");
        var vm = new WidgetViewModel([tab]);

        var settings = new AppSettings
        {
            WidgetOpacity = boundaryOpacity,
            WidgetAlwaysOnTop = true
        };

        vm.ApplySettings(settings);

        Assert.Equal(boundaryOpacity, vm.Opacity);
    }

    [Fact]
    public void ApplySettings_WhenValuesUnchanged_DoesNotFirePropertyChanged()
    {
        var tab = new ProviderTabViewModel("claude", "Claude");
        var vm = new WidgetViewModel([tab]);

        var initialSettings = new AppSettings
        {
            WidgetOpacity = 0.5,
            WidgetAlwaysOnTop = false
        };
        vm.ApplySettings(initialSettings);

        Assert.Equal(0.5, vm.Opacity);
        Assert.False(vm.AlwaysOnTop);

        var changedProps = new List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is not null)
            {
                changedProps.Add(e.PropertyName);
            }
        };

        // Re-applying identical settings must not raise property-changed
        var duplicateSettings = new AppSettings
        {
            WidgetOpacity = 0.5,
            WidgetAlwaysOnTop = false
        };
        vm.ApplySettings(duplicateSettings);

        Assert.Empty(changedProps);
    }

    [Fact]
    public void UpdateLayout_NonDefaultDimensions_RecalculatesVisibleMetersAndNewProperties()
    {
        var tab = new ProviderTabViewModel("claude", "Claude")
        {
            Status = "Connected",
            LastUpdated = DateTimeOffset.UtcNow,
            Meters =
            [
                new MeterDisplayItem { Title = "M1", ValueText = "10%" },
                new MeterDisplayItem { Title = "M2", ValueText = "20%" },
                new MeterDisplayItem { Title = "M3", ValueText = "30%" }
            ]
        };

        var vm = new WidgetViewModel([tab]);

        // Default layout (230x175) fits 3 meters.
        // Update to non-default narrow width 150: fits at most 2 meters.
        double width = 150.0;
        double height = 180.0;
        double headerHeight = 15.0;
        double lineHeight = 12.0;

        vm.UpdateLayout(width, height, headerHeight, lineHeight);

        Assert.Equal(2, vm.VisibleMeters.Count);
        Assert.True(vm.ArcSize >= WidgetLayout.ArcMin);
        Assert.True(vm.Cell > 0);
        Assert.Equal(2, vm.CaptionLines);
        Assert.Equal(lineHeight, vm.LineHeight);
        Assert.Equal(headerHeight, vm.HeaderHeight);
        Assert.Equal(headerHeight + 5.0, vm.HeaderRowHeight);
        Assert.Equal(lineHeight + 4.0, vm.FooterRowHeight);
        Assert.True(vm.ShowSubtitle);
        Assert.True(vm.InlineValue);
        Assert.False(vm.IsTooSmall);

        // With no reading yet there is no "updated … ago" line -> FooterRowHeight must be 0
        tab.LastUpdated = null;
        Assert.Equal(0.0, vm.FooterRowHeight);

        // Widen to non-default width 300 -> fits 3 meters
        vm.UpdateLayout(width: 300.0, height: 200.0, headerHeight, lineHeight);
        Assert.Equal(3, vm.VisibleMeters.Count);
    }

    [Fact]
    public void UpdateLayout_IdenticalInputs_PreservesVisibleMetersInstanceWithoutRaisingPropertyChanged()
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
        vm.UpdateLayout(width: 300.0, height: 200.0, headerHeight: 15.0, lineHeight: 12.0);

        var firstVisibleMeters = vm.VisibleMeters;
        Assert.Equal(3, firstVisibleMeters.Count);

        var changedProps = new List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is not null)
            {
                changedProps.Add(e.PropertyName);
            }
        };

        // Second call with identical inputs must keep the same instance and raise no VisibleMeters PropertyChanged
        vm.UpdateLayout(width: 300.0, height: 200.0, headerHeight: 15.0, lineHeight: 12.0);

        Assert.Same(firstVisibleMeters, vm.VisibleMeters);
        Assert.DoesNotContain(nameof(WidgetViewModel.VisibleMeters), changedProps);

        // Changing width so meter count drops to 2 must create a new instance and raise PropertyChanged
        vm.UpdateLayout(width: 150.0, height: 200.0, headerHeight: 15.0, lineHeight: 12.0);

        Assert.NotSame(firstVisibleMeters, vm.VisibleMeters);
        Assert.Equal(2, vm.VisibleMeters.Count);
        Assert.Contains(nameof(WidgetViewModel.VisibleMeters), changedProps);
    }

    [Fact]
    public void CurrentProvider_Changed_RecalculatesLayoutWithRememberedNonDefaultDimensions()
    {
        var tab1 = new ProviderTabViewModel("claude", "Claude")
        {
            Meters =
            [
                new MeterDisplayItem { Title = "C1", ValueText = "10%" },
                new MeterDisplayItem { Title = "C2", ValueText = "20%" },
                new MeterDisplayItem { Title = "C3", ValueText = "30%" }
            ]
        };

        var tab2 = new ProviderTabViewModel("openai", "OpenAI")
        {
            Meters =
            [
                new MeterDisplayItem { Title = "O1", ValueText = "40%" },
                new MeterDisplayItem { Title = "O2", ValueText = "50%" },
                new MeterDisplayItem { Title = "O3", ValueText = "60%" }
            ]
        };

        var vm = new WidgetViewModel([tab1, tab2]);

        // At width 150, fits only 2 of 3 meters
        vm.UpdateLayout(width: 150.0, height: 180.0, headerHeight: 15.0, lineHeight: 12.0);
        Assert.Equal(2, vm.VisibleMeters.Count);

        // NextProvider rotates to tab2 (which has 3 meters):
        // Must still show at most 2 of 3 meters because remembered width 150 is preserved!
        vm.NextProvider();
        Assert.Same(tab2, vm.CurrentProvider);
        Assert.Equal(2, vm.VisibleMeters.Count);
        Assert.Equal("O1", vm.VisibleMeters[0].Title);
        Assert.Equal("O2", vm.VisibleMeters[1].Title);

        // PreviousProvider returns to tab1 (which has 3 meters):
        // Still shows at most 2 of 3 meters
        vm.PreviousProvider();
        Assert.Same(tab1, vm.CurrentProvider);
        Assert.Equal(2, vm.VisibleMeters.Count);
        Assert.Equal("C1", vm.VisibleMeters[0].Title);
        Assert.Equal("C2", vm.VisibleMeters[1].Title);
    }

    [Fact]
    public void CurrentProvider_MetersWithoutValue_AreNotCountedOrDisplayed()
    {
        var tab = new ProviderTabViewModel("claude", "Claude")
        {
            Meters =
            [
                new MeterDisplayItem { Title = "Valid", ValueText = "45%" },
                new MeterDisplayItem { Title = "NoValuePlaceholder", ValueText = "--" },
                new MeterDisplayItem { Title = "EmptyValue", ValueText = "" },
                new MeterDisplayItem { Title = "WhitespaceValue", ValueText = "   " }
            ]
        };

        var vm = new WidgetViewModel([tab]);
        vm.UpdateLayout(width: 230.0, height: 175.0, headerHeight: 15.0, lineHeight: 12.0);

        // Only "Valid" (ValueText = "45%") must be counted and shown
        Assert.Single(vm.VisibleMeters);
        Assert.Equal("Valid", vm.VisibleMeters[0].Title);
        Assert.Equal("45%", vm.VisibleMeters[0].ValueText);
    }

    [Fact]
    public void CurrentProvider_MetersPropertyUpdate_TriggersRecalculation()
    {
        var tab = new ProviderTabViewModel("claude", "Claude")
        {
            Meters =
            [
                new MeterDisplayItem { Title = "M1", ValueText = "10%" }
            ]
        };

        var vm = new WidgetViewModel([tab]);
        vm.UpdateLayout(width: 230.0, height: 175.0, headerHeight: 15.0, lineHeight: 12.0);

        Assert.Single(vm.VisibleMeters);

        // Simulate snapshot update updating Meters collection
        tab.Meters =
        [
            new MeterDisplayItem { Title = "M1", ValueText = "10%" },
            new MeterDisplayItem { Title = "M2", ValueText = "20%" },
            new MeterDisplayItem { Title = "M3", ValueText = "30%" }
        ];

        Assert.Equal(3, vm.VisibleMeters.Count);
    }
}
