using AIMonitor.Application.Settings;
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
}
