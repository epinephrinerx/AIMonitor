using System.IO;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class LiveSettingsApplierTests
{
    [Fact]
    public async Task Apply_WhenShowTrayIconTrue_Twice_CreatesTrayOnlyOnce()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        var createCount = 0;
        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () =>
            {
                createCount++;
                return new FakeTrayHost();
            },
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        applier.Apply(new AppSettings { ShowTrayIcon = true });
        applier.Apply(new AppSettings { ShowTrayIcon = true });

        Assert.Equal(1, createCount);
        Assert.NotNull(applier.CurrentTray);
    }

    [Fact]
    public async Task Apply_WhenShowTrayIconTransitionsFromTrueToFalse_DetachesThenDisposesInOrder()
    {
        await using var coordinator = CreateCoordinatorWithRealData("claude", 80.0);
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        // Pre-populate cached readings in the main view model
        await mainViewModel.RefreshAsync();

        var fakeTray = new FakeTrayHost();
        fakeTray.OnDisposing = () =>
        {
            // If mainViewModel was already detached, this refresh will not push updates to fakeTray
            mainViewModel.RefreshAsync().GetAwaiter().GetResult();
        };

        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => fakeTray,
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        // 1. Initial attach
        applier.Apply(new AppSettings { ShowTrayIcon = true });
        Assert.Same(fakeTray, applier.CurrentTray);
        Assert.Single(fakeTray.RecordedUpdates);

        // 2. Transition from true -> false
        applier.Apply(new AppSettings { ShowTrayIcon = false });

        // Acceptance criteria: detaches then disposes exactly once, in that order (T6)
        Assert.Empty(fakeTray.UpdatesAfterDispose);
        Assert.Equal(1, fakeTray.DisposeCount);
        Assert.Null(applier.CurrentTray);

        // Acceptance criteria: MainWindowViewModel stops pushing readings to the old tray
        await mainViewModel.RefreshAsync();
        Assert.Empty(fakeTray.UpdatesAfterDispose);
        Assert.Single(fakeTray.RecordedUpdates);
    }

    [Fact]
    public async Task Apply_WhenShowTrayIconTransitionsFromFalseToTrue_CreatesNewTrayAndReplaysCachedReadings()
    {
        await using var coordinator = CreateCoordinatorWithRealData("claude", 80.0);
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        // Pre-populate cached readings before any tray is active
        await mainViewModel.RefreshAsync();

        var createdTrays = new List<FakeTrayHost>();
        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () =>
            {
                var tray = new FakeTrayHost();
                createdTrays.Add(tray);
                return tray;
            },
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        // Start with tray disabled
        applier.Apply(new AppSettings { ShowTrayIcon = false });
        Assert.Null(applier.CurrentTray);
        Assert.Empty(createdTrays);

        // Enable tray: false -> true
        applier.Apply(new AppSettings { ShowTrayIcon = true });

        Assert.Single(createdTrays);
        var activeTray = createdTrays[0];
        Assert.Same(activeTray, applier.CurrentTray);

        // Acceptance criteria: latest cached readings are replayed to the new tray immediately with content asserted (T3)
        var replayed = Assert.Single(activeTray.RecordedUpdates);
        var reading = Assert.Single(replayed, r => r.HasData);
        Assert.Equal("claude", reading.ProviderId);
        Assert.Equal(80.0, reading.Percentage);
    }

    [Fact]
    public async Task Apply_UpdatesWidgetOpacityAndAlwaysOnTop_OnEveryCall()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        var tab = new ProviderTabViewModel("claude", "Claude");
        var widgetVm = new WidgetViewModel([tab]);

        var applier = new LiveSettingsApplier(
            widgetViewModel: widgetVm,
            mainViewModel: mainViewModel,
            createTray: () => new FakeTrayHost(),
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        // First apply
        applier.Apply(new AppSettings
        {
            WidgetOpacity = 0.55,
            WidgetAlwaysOnTop = false
        });

        Assert.Equal(0.55, widgetVm.Opacity);
        Assert.False(widgetVm.AlwaysOnTop);

        // Second apply with updated settings
        applier.Apply(new AppSettings
        {
            WidgetOpacity = 0.85,
            WidgetAlwaysOnTop = true
        });

        Assert.Equal(0.85, widgetVm.Opacity);
        Assert.True(widgetVm.AlwaysOnTop);
    }

    [Fact]
    public async Task Apply_InvokesRefreshIntervalCallbackWithConfiguredSeconds()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        TimeSpan? configuredInterval = null;
        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => new FakeTrayHost(),
            setRefreshInterval: interval => configuredInterval = interval,
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        applier.Apply(new AppSettings { RefreshIntervalSeconds = 45 });
        Assert.Equal(TimeSpan.FromSeconds(45), configuredInterval);

        applier.Apply(new AppSettings { RefreshIntervalSeconds = 120 });
        Assert.Equal(TimeSpan.FromSeconds(120), configuredInterval);
    }

    [Fact]
    public async Task Apply_WhenWidgetViewModelIsNull_ToleratesNullAndAppliesOtherSettings()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        TimeSpan? configuredInterval = null;
        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => new FakeTrayHost(),
            setRefreshInterval: interval => configuredInterval = interval,
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 90,
            ShowTrayIcon = true
        };

        var exception = Record.Exception(() => applier.Apply(settings));

        Assert.Null(exception);
        Assert.NotNull(applier.CurrentTray);
        Assert.Equal(TimeSpan.FromSeconds(90), configuredInterval);
    }

    [Fact]
    public async Task Apply_WhenShowTrayIconTransitionsFromTrueToFalse_WithNoWindowVisible_CallsShowDashboardOnceAfterDispose()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        var fakeTray = new FakeTrayHost();
        var showDashboardCallCount = 0;
        var showDashboardCalledAfterDispose = false;

        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => fakeTray,
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () =>
            {
                showDashboardCallCount++;
                if (fakeTray.IsDisposed)
                {
                    showDashboardCalledAfterDispose = true;
                }
            });

        // 1. Enable tray
        applier.Apply(new AppSettings { ShowTrayIcon = true });
        Assert.Same(fakeTray, applier.CurrentTray);
        Assert.Equal(0, showDashboardCallCount);

        // 2. Tray transitions from true -> false with no window visible
        applier.Apply(new AppSettings { ShowTrayIcon = false });

        Assert.Equal(1, showDashboardCallCount);
        Assert.True(showDashboardCalledAfterDispose, "showDashboard must be invoked AFTER tray is disposed.");
    }

    [Fact]
    public async Task Apply_WhenShowTrayIconTransitionsFromTrueToFalse_WithWindowVisible_DoesNotCallShowDashboard()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        var fakeTray = new FakeTrayHost();
        var showDashboardCallCount = 0;

        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => fakeTray,
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => true,
            showDashboard: () => showDashboardCallCount++);

        applier.Apply(new AppSettings { ShowTrayIcon = true });
        applier.Apply(new AppSettings { ShowTrayIcon = false });

        Assert.Equal(0, showDashboardCallCount);
    }

    [Fact]
    public async Task Apply_WhenShowTrayIconTransitionsFromFalseToTrue_NeverCallsShowDashboard()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        var showDashboardCallCount = 0;
        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => new FakeTrayHost(),
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () => showDashboardCallCount++);

        applier.Apply(new AppSettings { ShowTrayIcon = false });
        applier.Apply(new AppSettings { ShowTrayIcon = true });

        Assert.Equal(0, showDashboardCallCount);
    }

    [Fact]
    public async Task Apply_SetRefreshInterval_OnlyInvokedWhenIntervalDiffers()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        var callbackCount = 0;
        TimeSpan? lastInterval = null;

        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => new FakeTrayHost(),
            setRefreshInterval: interval =>
            {
                callbackCount++;
                lastInterval = interval;
            },
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        // First apply with 45s: callback invoked once
        applier.Apply(new AppSettings { RefreshIntervalSeconds = 45 });
        Assert.Equal(1, callbackCount);
        Assert.Equal(TimeSpan.FromSeconds(45), lastInterval);

        // Second apply with identical interval: callback NOT invoked again
        applier.Apply(new AppSettings { RefreshIntervalSeconds = 45 });
        Assert.Equal(1, callbackCount);

        // Third apply with changed interval (90s): callback invoked again
        applier.Apply(new AppSettings { RefreshIntervalSeconds = 90 });
        Assert.Equal(2, callbackCount);
        Assert.Equal(TimeSpan.FromSeconds(90), lastInterval);

        // Fourth apply with identical interval (90s): callback NOT invoked again
        applier.Apply(new AppSettings { RefreshIntervalSeconds = 90 });
        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public async Task Shutdown_DetachesThenDisposes_IsIdempotent_AndClearsCurrentTray()
    {
        await using var coordinator = CreateCoordinatorWithRealData("claude", 80.0);
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);
        await mainViewModel.RefreshAsync();

        var fakeTray = new FakeTrayHost();
        fakeTray.OnDisposing = () =>
        {
            // If mainViewModel was already detached, this refresh will NOT push updates to fakeTray
            mainViewModel.RefreshAsync().GetAwaiter().GetResult();
        };

        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => fakeTray,
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        applier.Apply(new AppSettings { ShowTrayIcon = true });
        Assert.Same(fakeTray, applier.CurrentTray);

        // Shutdown
        applier.Shutdown();

        Assert.Null(applier.CurrentTray);
        Assert.Equal(1, fakeTray.DisposeCount);
        Assert.Empty(fakeTray.UpdatesAfterDispose);

        // Idempotent: second shutdown call does not dispose again or throw
        applier.Shutdown();
        Assert.Null(applier.CurrentTray);
        Assert.Equal(1, fakeTray.DisposeCount);

        // Refreshing after shutdown sends no updates to the disposed tray
        await mainViewModel.RefreshAsync();
        Assert.Empty(fakeTray.UpdatesAfterDispose);
    }

    [Fact]
    public async Task Apply_FullCycle_TrueFalseTrue_CreatesDistinctTrayAndReplaysCachedReadingsWithCorrectContent()
    {
        await using var coordinator = CreateCoordinatorWithRealData("claude", 80.0);
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);
        await mainViewModel.RefreshAsync();

        var createdTrays = new List<FakeTrayHost>();
        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () =>
            {
                var tray = new FakeTrayHost();
                createdTrays.Add(tray);
                return tray;
            },
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => false,
            showDashboard: () => { });

        // 1. Initial true
        applier.Apply(new AppSettings { ShowTrayIcon = true });
        Assert.Single(createdTrays);
        var tray1 = createdTrays[0];
        Assert.Same(tray1, applier.CurrentTray);

        // 2. Transition to false
        applier.Apply(new AppSettings { ShowTrayIcon = false });
        Assert.Null(applier.CurrentTray);
        Assert.Equal(1, tray1.DisposeCount);

        // 3. Transition back to true
        applier.Apply(new AppSettings { ShowTrayIcon = true });
        Assert.Equal(2, createdTrays.Count);
        var tray2 = createdTrays[1];
        Assert.Same(tray2, applier.CurrentTray);
        Assert.NotSame(tray1, tray2);
        Assert.Equal(0, tray2.DisposeCount);

        // Second tray receives replay with correct content
        var replayed = Assert.Single(tray2.RecordedUpdates);
        var reading = Assert.Single(replayed, r => r.HasData);
        Assert.Equal("claude", reading.ProviderId);
        Assert.Equal(80.0, reading.Percentage);
    }

    [Fact]
    public void AppWiringContract_OnSettingsChangedBodyAppliesSettings_AndShouldStartHiddenIsPreserved()
    {
        var solutionRoot = FindSolutionRoot();
        var appXamlCsPath = Path.Combine(solutionRoot, "src", "AIMonitor.Presentation.Wpf", "App.xaml.cs");
        Assert.True(File.Exists(appXamlCsPath), $"App.xaml.cs not found at: {appXamlCsPath}");

        var fileContent = File.ReadAllText(appXamlCsPath);

        // 1. Assert file contains TrayPolicy.ShouldStartHidden and session event subscription
        Assert.Contains("TrayPolicy.ShouldStartHidden", fileContent);
        Assert.Contains("_settingsSession.Changed += OnSettingsChanged;", fileContent);

        // 2. Extract the BODY of OnSettingsChanged method via brace matching
        const string methodSignature = "OnSettingsChanged(AppSettings settings)";
        var methodIndex = fileContent.IndexOf(methodSignature, StringComparison.Ordinal);
        if (methodIndex < 0)
        {
            methodIndex = fileContent.IndexOf("OnSettingsChanged", StringComparison.Ordinal);
        }
        Assert.True(methodIndex >= 0, "Method OnSettingsChanged was not found in App.xaml.cs");

        var openBraceIndex = fileContent.IndexOf('{', methodIndex);
        Assert.True(openBraceIndex >= 0, "Opening brace for OnSettingsChanged was not found in App.xaml.cs.");

        var braceDepth = 0;
        var closeBraceIndex = -1;
        for (var i = openBraceIndex; i < fileContent.Length; i++)
        {
            if (fileContent[i] == '{')
            {
                braceDepth++;
            }
            else if (fileContent[i] == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                {
                    closeBraceIndex = i;
                    break;
                }
            }
        }

        Assert.True(closeBraceIndex > openBraceIndex, "Matching closing brace for OnSettingsChanged was not found in App.xaml.cs via brace matching.");

        var methodBody = fileContent.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);

        // 3. Assert the body of OnSettingsChanged contains Dispatcher.InvokeAsync and invokes .Apply( inside the lambda
        Assert.Contains("Dispatcher.InvokeAsync", methodBody);

        var invokeAsyncIndex = methodBody.IndexOf("Dispatcher.InvokeAsync", StringComparison.Ordinal);
        Assert.True(invokeAsyncIndex >= 0, "Dispatcher.InvokeAsync must be present in OnSettingsChanged body.");

        var applyIndex = methodBody.IndexOf(".Apply(", invokeAsyncIndex, StringComparison.Ordinal);
        Assert.True(applyIndex > invokeAsyncIndex, "Expected .Apply( to occur inside the Dispatcher.InvokeAsync lambda (after Dispatcher.InvokeAsync).");
    }

    private static LatestRefreshCoordinator CreateCoordinatorWithRealData(string providerId = "claude", double percent = 80.0)
    {
        var snapshot = new ProviderSnapshot(
            providerId: providerId,
            configured: true,
            meters: [new Meter("session", "session", "Session Quota", "", percent)]);

        var client = new TestQuotaClient((req, ct) => Task.FromResult(snapshot));
        var registrations = new[] { new ProviderClientRegistration(providerId, client) };
        return new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AIMonitor.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find AIMonitor.sln by walking up from AppDomain.CurrentDomain.BaseDirectory.");
    }

    private sealed class FakeTrayHost : ITrayHost
    {
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }
        public List<IReadOnlyList<TrayReading>> RecordedUpdates { get; } = [];
        public List<IReadOnlyList<TrayReading>> UpdatesAfterDispose { get; } = [];
        public Action? OnDisposing { get; set; }

        public void UpdateReadings(IEnumerable<TrayReading> readings)
        {
            var copy = readings.ToList();
            RecordedUpdates.Add(copy);
            if (IsDisposed)
            {
                UpdatesAfterDispose.Add(copy);
            }
        }

        public void Dispose()
        {
            IsDisposed = true;
            DisposeCount++;
            OnDisposing?.Invoke();
        }
    }

    private sealed class TestQuotaClient : IProviderQuotaClient
    {
        public Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> Handler { get; set; }

        public TestQuotaClient(Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> handler)
        {
            Handler = handler;
        }

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
            => Handler(request, cancellationToken);
    }
}
