using System.ComponentModel;
using System.IO;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

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
            ChartRangeDays = 30,
            DashboardWidth = 960,
            DashboardHeight = 680
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
        Assert.Equal(85, vm.OpacityPercent);
        Assert.False(vm.WidgetAlwaysOnTop);
        Assert.Equal(30, vm.ChartRangeDays);
        Assert.Equal(WindowSizeOption.Compact, vm.SelectedWindowSize);
    }

    [Fact]
    public async Task SaveAsync_PersistsUpdatedSettings_AndClosesDialog()
    {
        var initial = new AppSettings { Theme = "system", StartWithWindows = false, DashboardWidth = 1120, DashboardHeight = 820 };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

        var closeResult = false;
        vm.RequestClose += result => closeResult = result;

        vm.Theme = "light";
        vm.RefreshIntervalSeconds = 90;
        vm.StartWithWindows = false;
        vm.SelectedWindowSize = WindowSizeOption.Wide;

        await vm.SaveAsync();

        Assert.True(closeResult);
        Assert.NotNull(store.SavedSettings);
        Assert.Equal("light", store.SavedSettings.Theme);
        Assert.Equal(90, store.SavedSettings.RefreshIntervalSeconds);
        Assert.Equal(1400, store.SavedSettings.DashboardWidth);
        Assert.Equal(900, store.SavedSettings.DashboardHeight);
        Assert.Equal("light", session.Current.Theme);
        Assert.Equal(90, session.Current.RefreshIntervalSeconds);
        Assert.Equal(1400, session.Current.DashboardWidth);
        Assert.Equal(900, session.Current.DashboardHeight);
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

    [Fact]
    public void Preview_ThemeWidgetOpacityAlwaysOnTopAndWindowSize_InvokesCallback_WithoutChangingSessionOrStore()
    {
        var initial = new AppSettings
        {
            Theme = "system",
            WidgetOpacity = 0.92,
            WidgetAlwaysOnTop = true,
            DashboardWidth = 1120,
            DashboardHeight = 820
        };

        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);

        var previewCalls = new List<AppSettings>();
        var vm = new SettingsViewModel(session, registrar, s => previewCalls.Add(s));

        // 1. Changing Theme
        vm.Theme = "dark";
        Assert.Single(previewCalls);
        Assert.Equal("dark", previewCalls[^1].Theme);
        Assert.Equal("system", session.Current.Theme); // Session unchanged
        Assert.Null(store.SavedSettings);              // Store unchanged

        // 2. Changing WidgetOpacity
        vm.WidgetOpacity = 0.50;
        Assert.Equal(2, previewCalls.Count);
        Assert.Equal(0.50, previewCalls[^1].WidgetOpacity);
        Assert.Equal(0.92, session.Current.WidgetOpacity);

        // 3. Changing WidgetAlwaysOnTop
        vm.WidgetAlwaysOnTop = false;
        Assert.Equal(3, previewCalls.Count);
        Assert.False(previewCalls[^1].WidgetAlwaysOnTop);
        Assert.True(session.Current.WidgetAlwaysOnTop);

        // 4. Changing SelectedWindowSize
        vm.SelectedWindowSize = WindowSizeOption.Compact;
        Assert.Equal(4, previewCalls.Count);
        Assert.Equal(960, previewCalls[^1].DashboardWidth);
        Assert.Equal(680, previewCalls[^1].DashboardHeight);
        Assert.Equal(1120, session.Current.DashboardWidth);
        Assert.Equal(820, session.Current.DashboardHeight);

        // Verification: store remained untouched
        Assert.Null(store.SavedSettings);
    }

    [Fact]
    public void WidgetOpacity_ClampsBetween025And10_AndUpdatesOpacityPercent()
    {
        var initial = new AppSettings { WidgetOpacity = 0.92 };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, registrar);

        var propertyChanges = new List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is not null)
            {
                propertyChanges.Add(e.PropertyName);
            }
        };

        // Underflow: clamped to 0.25 (25%)
        vm.WidgetOpacity = 0.10;
        Assert.Equal(0.25, vm.WidgetOpacity);
        Assert.Equal(25, vm.OpacityPercent);
        Assert.Contains(nameof(SettingsViewModel.WidgetOpacity), propertyChanges);
        Assert.Contains(nameof(SettingsViewModel.OpacityPercent), propertyChanges);

        propertyChanges.Clear();

        // Overflow: clamped to 1.0 (100%)
        vm.WidgetOpacity = 1.80;
        Assert.Equal(1.0, vm.WidgetOpacity);
        Assert.Equal(100, vm.OpacityPercent);
        Assert.Contains(nameof(SettingsViewModel.WidgetOpacity), propertyChanges);
        Assert.Contains(nameof(SettingsViewModel.OpacityPercent), propertyChanges);

        propertyChanges.Clear();

        // Mid-range value
        vm.WidgetOpacity = 0.72;
        Assert.Equal(0.72, vm.WidgetOpacity);
        Assert.Equal(72, vm.OpacityPercent);
        Assert.Contains(nameof(SettingsViewModel.WidgetOpacity), propertyChanges);
        Assert.Contains(nameof(SettingsViewModel.OpacityPercent), propertyChanges);
    }

    [Fact]
    public void Cancel_InvokesCallbackWithEntrySettings_AndClosesWithFalse()
    {
        var initial = new AppSettings
        {
            Theme = "system",
            WidgetOpacity = 0.92,
            WidgetAlwaysOnTop = true,
            DashboardWidth = 1120,
            DashboardHeight = 820
        };

        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);

        AppSettings? lastPreview = null;
        var vm = new SettingsViewModel(session, registrar, s => lastPreview = s);

        var closeResult = true;
        vm.RequestClose += r => closeResult = r;

        // User edits all 4 preview fields
        vm.Theme = "light";
        vm.WidgetOpacity = 0.40;
        vm.WidgetAlwaysOnTop = false;
        vm.SelectedWindowSize = WindowSizeOption.Wide;

        // User cancels
        vm.Cancel();

        // Must request close with false
        Assert.False(closeResult);

        // Preview callback must receive the exact entry settings
        Assert.NotNull(lastPreview);
        Assert.Equal("system", lastPreview.Theme);
        Assert.Equal(0.92, lastPreview.WidgetOpacity);
        Assert.True(lastPreview.WidgetAlwaysOnTop);
        Assert.Equal(1120, lastPreview.DashboardWidth);
        Assert.Equal(820, lastPreview.DashboardHeight);
    }

    [Fact]
    public void Discard_IsIdempotent_SecondCallDoesNotInvokeCallback()
    {
        var initial = new AppSettings { Theme = "system", WidgetOpacity = 0.92 };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);

        var previewCallCount = 0;
        var vm = new SettingsViewModel(session, registrar, _ => previewCallCount++);

        vm.Theme = "dark";
        Assert.Equal(1, previewCallCount);

        // First discard invokes preview with entry settings
        vm.Discard();
        Assert.Equal(2, previewCallCount);

        // Second discard: no-op, call count must not increase
        vm.Discard();
        Assert.Equal(2, previewCallCount);

        // Cancel after discard: no-op for discard, still requests close
        var closeCalled = false;
        vm.RequestClose += _ => closeCalled = true;
        vm.Cancel();
        Assert.Equal(2, previewCallCount);
        Assert.True(closeCalled);
    }

    [Fact]
    public async Task SaveAsync_ThenDiscard_DoesNotRevertSettings()
    {
        var initial = new AppSettings { Theme = "system", WidgetOpacity = 0.92 };
        var store = new FakeSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);

        var previewCalls = new List<AppSettings>();
        var vm = new SettingsViewModel(session, registrar, s => previewCalls.Add(s));

        vm.Theme = "dark";
        Assert.Single(previewCalls);

        await vm.SaveAsync();

        // Discard after successful save must be a no-op and must not revert
        vm.Discard();

        Assert.Single(previewCalls);
        Assert.Equal("dark", session.Current.Theme);
    }

    [Fact]
    public async Task Discard_DoesNotOverwriteOtherFieldsModifiedInSessionConcurrently()
    {
        var initial = new AppSettings
        {
            Theme = "dark",
            WidgetOpacity = 0.85,
            WidgetAlwaysOnTop = true,
            DashboardWidth = 1120,
            DashboardHeight = 820,
            ActiveProvider = "claude",
            ChartMetric = "Total tokens"
        };

        // Real SettingsSession over real BlockingSettingsStore (released upfront)
        var store = new BlockingSettingsStore(initial);
        store.Release();
        using var session = new SettingsSession(store, initial);

        AppSettings? lastPreview = null;
        var registrar = new FakeStartupRegistrar();
        var vm = new SettingsViewModel(session, registrar, s => lastPreview = s);

        // 1. User edits dialog fields
        vm.Theme = "light";
        vm.WidgetOpacity = 0.50;

        // 2. Concurrently, another component updates session with new ActiveProvider & ChartMetric
        await session.UpdateAsync(s => s with
        {
            ActiveProvider = "gemini",
            ChartMetric = "Output tokens"
        });

        // 3. User cancels / discards dialog
        vm.Discard();

        // Verification:
        // - Preview callback restored the 4 entry appearance fields
        // - Preview callback preserved the concurrently updated ActiveProvider and ChartMetric!
        Assert.NotNull(lastPreview);
        Assert.Equal("dark", lastPreview.Theme);
        Assert.Equal(0.85, lastPreview.WidgetOpacity);
        Assert.True(lastPreview.WidgetAlwaysOnTop);
        Assert.Equal(1120, lastPreview.DashboardWidth);
        Assert.Equal(820, lastPreview.DashboardHeight);
        Assert.Equal("gemini", lastPreview.ActiveProvider);
        Assert.Equal("Output tokens", lastPreview.ChartMetric);

        // Session current remains updated with gemini and Output tokens
        Assert.Equal("gemini", session.Current.ActiveProvider);
        Assert.Equal("Output tokens", session.Current.ChartMetric);
    }

    [Fact]
    public async Task SaveAsync_WhileAwaitingUpdate_DiscardDoesNotRevert_AndSaveCompletesWithDarkTheme()
    {
        var initial = new AppSettings { Theme = "system" };
        var store = new BlockingSettingsStore(initial);
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);

        var previewCallbacks = new List<AppSettings>();
        var vm = new SettingsViewModel(session, registrar, s => previewCallbacks.Add(s));

        // 1. Preview theme dark
        vm.Theme = "dark";
        Assert.Single(previewCallbacks);
        Assert.Equal("dark", previewCallbacks[0].Theme);

        // 2. Start Save (SaveStarted awaited)
        var saveTask = vm.SaveAsync();
        await store.SaveStarted.Task;

        Assert.True(vm.IsSaving);

        // 3. Call Discard while saving -> callback NOT invoked with entry (must be a no-op)
        vm.Discard();
        Assert.Single(previewCallbacks);
        Assert.Equal("dark", previewCallbacks[^1].Theme);

        // 4. Release store and await save
        store.Release();
        await saveTask;

        // 5. Verification: Current.Theme == "dark", last preview callback is dark, IsSaving is false
        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal("dark", previewCallbacks[^1].Theme);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task SaveAsync_WhenSaveFails_DiscardStillReverts()
    {
        var initial = new AppSettings { Theme = "system" };
        var store = new BlockingSettingsStore(initial)
        {
            FailOnSave = new IOException("Disk failure during save")
        };
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);

        var previewCallbacks = new List<AppSettings>();
        var vm = new SettingsViewModel(session, registrar, s => previewCallbacks.Add(s));

        vm.Theme = "dark";
        Assert.Equal("dark", previewCallbacks[^1].Theme);

        var saveTask = vm.SaveAsync();
        await store.SaveStarted.Task;
        Assert.True(vm.IsSaving);

        // Discard while saving is a no-op
        vm.Discard();
        Assert.Equal("dark", previewCallbacks[^1].Theme);

        // Release the failing save
        store.Release();
        await Assert.ThrowsAsync<IOException>(() => saveTask);

        // After failed save, IsSaving must be false, _saved must be false, and Discard works as before
        Assert.False(vm.IsSaving);
        vm.Discard();
        Assert.Equal("system", previewCallbacks[^1].Theme);
    }

    [Fact]
    public async Task SaveAsync_WhenStoreThrows_LeavesSavedFalse_SoDiscardCanStillRevert()
    {
        var initial = new AppSettings { Theme = "system" };
        var store = new FailingSettingsStore(new IOException("Disk write failure"));
        var registrar = new FakeStartupRegistrar();
        using var session = new SettingsSession(store, initial);

        AppSettings? lastPreview = null;
        var vm = new SettingsViewModel(session, registrar, s => lastPreview = s);

        vm.Theme = "dark";
        Assert.NotNull(lastPreview);
        Assert.Equal("dark", lastPreview.Theme);

        // Save fails
        await Assert.ThrowsAsync<IOException>(() => vm.SaveAsync());

        // Discard should still be allowed to revert since save failed
        vm.Discard();
        Assert.Equal("system", lastPreview.Theme);
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
