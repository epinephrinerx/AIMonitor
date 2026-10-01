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
}
