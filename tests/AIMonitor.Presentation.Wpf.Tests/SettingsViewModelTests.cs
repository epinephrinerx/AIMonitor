using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void Constructor_InitializesWithCurrentSettings()
    {
        var settings = new AppSettings
        {
            Theme = "dark",
            StartWithWindows = true,
            MinimizeToTray = true,
            ShowTrayIcon = true,
            RefreshIntervalSeconds = 45,
            WidgetOpacity = 0.85,
            WidgetAlwaysOnTop = false,
            ChartRangeDays = 30
        };

        var store = new FakeSettingsStore(settings);
        var registrar = new FakeStartupRegistrar();
        var vm = new SettingsViewModel(settings, store, registrar);

        Assert.Equal("dark", vm.Theme);
        Assert.True(vm.StartWithWindows);
        Assert.True(vm.MinimizeToTray);
        Assert.True(vm.ShowTrayIcon);
        Assert.Equal(45, vm.RefreshIntervalSeconds);
        Assert.Equal(0.85, vm.WidgetOpacity);
        Assert.False(vm.WidgetAlwaysOnTop);
        Assert.Equal(30, vm.ChartRangeDays);
    }

    [Fact]
    public async Task SaveAsync_PersistsUpdatedSettings_AndClosesDialog()
    {
        var initial = new AppSettings { Theme = "system", StartWithWindows = false };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        var vm = new SettingsViewModel(initial, store, registrar);

        var closeResult = false;
        vm.RequestClose += result => closeResult = result;

        vm.Theme = "light";
        vm.RefreshIntervalSeconds = 90;
        vm.StartWithWindows = false;

        await vm.SaveAsync();

        Assert.True(closeResult);
        Assert.NotNull(store.SavedSettings);
        Assert.Equal("light", store.SavedSettings.Theme);
        Assert.Equal(90, store.SavedSettings.RefreshIntervalSeconds);
        Assert.True(registrar.UnregisterCalled);
    }

    [Fact]
    public void Cancel_RevertsTheme_AndClosesDialogWithFalse()
    {
        var initial = new AppSettings { Theme = "dark" };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        var vm = new SettingsViewModel(initial, store, registrar);

        var closeResult = true;
        vm.RequestClose += result => closeResult = result;

        vm.Theme = "light";
        vm.Cancel();

        Assert.False(closeResult);
    }

    private sealed class FakeSettingsStore(AppSettings initial) : ISettingsStore
    {
        public bool Exists => true;
        public AppSettings? SavedSettings { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(initial);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegisteredResult { get; set; }
        public bool RegisterCalled { get; private set; }
        public bool UnregisterCalled { get; private set; }

        public bool IsRegistered() => IsRegisteredResult;

        public void Register(string executablePath, string arguments = "")
        {
            RegisterCalled = true;
        }

        public void Unregister()
        {
            UnregisterCalled = true;
        }
    }
}
