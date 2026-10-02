using System.IO;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
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
        using var session = new SettingsSession(store, settings);
        var vm = new SettingsViewModel(session, registrar);

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
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

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
        Assert.Equal("light", session.Current.Theme);
        Assert.Equal(90, session.Current.RefreshIntervalSeconds);
        Assert.True(registrar.UnregisterCalled);
    }

    [Fact]
    public async Task SaveAsync_PreservesUnmanagedFieldsLikeGeometryAndProviders()
    {
        var initial = new AppSettings { Theme = "system", RefreshIntervalSeconds = 60 };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

        // Another writer changes geometry/providers/ActiveProvider AFTER view model was constructed
        await session.UpdateAsync(s => s with
        {
            ActiveProvider = "gemini",
            ChartMetric = "Output tokens",
            LegacyGeometry = new Dictionary<string, string> { ["d0123456789/dash/usedAt"] = "timestamp" },
            Providers = new Dictionary<string, ProviderPreference> { ["gemini"] = new(true, "extra-val") }
        });

        // User edits dialog fields and saves
        vm.Theme = "dark";
        vm.RefreshIntervalSeconds = 300;

        await vm.SaveAsync();

        // Fields written after construction must survive
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal(300, session.Current.RefreshIntervalSeconds);
        Assert.Equal("gemini", session.Current.ActiveProvider);
        Assert.Equal("Output tokens", session.Current.ChartMetric);
        Assert.True(session.Current.LegacyGeometry.ContainsKey("d0123456789/dash/usedAt"));
        Assert.True(session.Current.Providers.ContainsKey("gemini"));
    }

    [Fact]
    public void Cancel_RevertsTheme_AndClosesDialogWithFalse()
    {
        var initial = new AppSettings { Theme = "dark" };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

        var closeResult = true;
        vm.RequestClose += result => closeResult = result;

        vm.Theme = "light";
        vm.Cancel();

        Assert.False(closeResult);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsIOException_DoesNotClose_RaisesSaveFailed_AndKeepsCurrentUnchanged()
    {
        var initial = new AppSettings { Theme = "system", RefreshIntervalSeconds = 60 };
        var store = new FailingSettingsStore(new IOException("Disk write failure"));
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

        var closeRequested = false;
        vm.RequestClose += _ => closeRequested = true;

        var saveFailedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SaveFailed += msg => saveFailedTcs.TrySetResult(msg);

        vm.Theme = "dark";
        vm.SaveCommand.Execute(null);

        var message = await saveFailedTcs.Task;

        Assert.False(closeRequested);
        Assert.NotNull(message);
        Assert.Contains("I/O", message);
        Assert.Equal("system", session.Current.Theme);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsUnauthorizedAccessException_DoesNotClose_RaisesSaveFailed()
    {
        var initial = new AppSettings { Theme = "system" };
        var store = new FailingSettingsStore(new UnauthorizedAccessException("Access denied"));
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

        var closeRequested = false;
        vm.RequestClose += _ => closeRequested = true;

        var saveFailedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SaveFailed += msg => saveFailedTcs.TrySetResult(msg);

        vm.Theme = "dark";
        vm.SaveCommand.Execute(null);

        var message = await saveFailedTcs.Task;

        Assert.False(closeRequested);
        Assert.NotNull(message);
        Assert.Contains("Access denied", message);
        Assert.Equal("system", session.Current.Theme);
    }

    [Fact]
    public async Task SaveAsync_WhenStoreThrows_ThrowsToDirectCaller()
    {
        var initial = new AppSettings { Theme = "system" };
        var store = new FailingSettingsStore(new IOException("Disk write failure"));
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

        vm.Theme = "dark";
        var ex = await Assert.ThrowsAsync<IOException>(() => vm.SaveAsync());
        Assert.Equal("Disk write failure", ex.Message);
        Assert.Equal("system", session.Current.Theme);
    }

    private sealed class FailingSettingsStore(Exception exceptionToThrow) : ISettingsStore
    {
        public bool Exists => true;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(exceptionToThrow);
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
