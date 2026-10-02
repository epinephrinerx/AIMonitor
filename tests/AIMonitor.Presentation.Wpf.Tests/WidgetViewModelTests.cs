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
    public void UpdateLayout_WideWidth_ShowsAllValidMeters()
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
        vm.UpdateLayout(width: 230.0, height: 175.0, headerHeight: 15.0, lineHeight: 12.0);

        Assert.Equal(3, vm.VisibleMeters.Count);
        Assert.True(vm.ArcSize >= WidgetLayout.ArcMin);
        Assert.True(vm.ShowSubtitle);
        Assert.True(vm.InlineValue);
        Assert.False(vm.IsTooSmall);
    }

    [Fact]
    public void UpdateLayout_NarrowWidth_TruncatesVisibleMetersToComputedCount()
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
        // Narrow width 100px can only fit 1 meter
        vm.UpdateLayout(width: 100.0, height: 175.0, headerHeight: 15.0, lineHeight: 12.0);

        Assert.Single(vm.VisibleMeters);
        Assert.Equal("M1", vm.VisibleMeters[0].Title);
        Assert.Equal("10%", vm.VisibleMeters[0].ValueText);
    }

    [Fact]
    public void CurrentProvider_Changed_RecalculatesLayoutWithLatestDimensions()
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
                new MeterDisplayItem { Title = "O1", ValueText = "50%" }
            ]
        };

        var vm = new WidgetViewModel([tab1, tab2]);
        vm.UpdateLayout(width: 230.0, height: 175.0, headerHeight: 15.0, lineHeight: 12.0);

        Assert.Equal(3, vm.VisibleMeters.Count);

        // NextProvider rotates to tab2 with 1 meter
        vm.NextProvider();
        Assert.Same(tab2, vm.CurrentProvider);
        Assert.Single(vm.VisibleMeters);
        Assert.Equal("O1", vm.VisibleMeters[0].Title);

        // PreviousProvider returns to tab1 with 3 meters
        vm.PreviousProvider();
        Assert.Same(tab1, vm.CurrentProvider);
        Assert.Equal(3, vm.VisibleMeters.Count);
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
