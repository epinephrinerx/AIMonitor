using System.IO;
using System.Reflection;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
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
            setRefreshInterval: _ => { });

        applier.Apply(new AppSettings { ShowTrayIcon = true });
        applier.Apply(new AppSettings { ShowTrayIcon = true });

        Assert.Equal(1, createCount);
        Assert.NotNull(applier.CurrentTray);
    }

    [Fact]
    public async Task Apply_WhenShowTrayIconTransitionsFromTrueToFalse_DetachesThenDisposesInOrder()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var mainViewModel = new MainWindowViewModel(coordinator, session);

        // Pre-populate cached readings in the main view model
        await mainViewModel.RefreshAsync();

        var fakeTray = new FakeTrayHost();
        var wasDetachedBeforeDispose = false;
        fakeTray.OnDisposing = () =>
        {
            var trayField = typeof(MainWindowViewModel).GetField("_traySink", BindingFlags.NonPublic | BindingFlags.Instance);
            wasDetachedBeforeDispose = trayField?.GetValue(mainViewModel) is null;
        };

        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: mainViewModel,
            createTray: () => fakeTray,
            setRefreshInterval: _ => { });

        // 1. Initial attach
        applier.Apply(new AppSettings { ShowTrayIcon = true });
        Assert.Same(fakeTray, applier.CurrentTray);
        Assert.Single(fakeTray.RecordedUpdates);

        // 2. Transition from true -> false
        applier.Apply(new AppSettings { ShowTrayIcon = false });

        // Acceptance criteria: detaches then disposes exactly once, in that order
        Assert.True(wasDetachedBeforeDispose, "MainWindowViewModel.AttachTray(null) must be called before disposing the tray host.");
        Assert.Equal(1, fakeTray.DisposeCount);
        Assert.Null(applier.CurrentTray);

        // Acceptance criteria: MainWindowViewModel stops pushing readings to the old tray
        await mainViewModel.RefreshAsync();
        Assert.Single(fakeTray.RecordedUpdates);
    }

    [Fact]
    public async Task Apply_WhenShowTrayIconTransitionsFromFalseToTrue_CreatesNewTrayAndReplaysCachedReadings()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
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
            setRefreshInterval: _ => { });

        // Start with tray disabled
        applier.Apply(new AppSettings { ShowTrayIcon = false });
        Assert.Null(applier.CurrentTray);
        Assert.Empty(createdTrays);

        // Enable tray: false -> true
        applier.Apply(new AppSettings { ShowTrayIcon = true });

        Assert.Single(createdTrays);
        var activeTray = createdTrays[0];
        Assert.Same(activeTray, applier.CurrentTray);

        // Acceptance criteria: latest cached readings are replayed to the new tray immediately
        Assert.Single(activeTray.RecordedUpdates);
        Assert.NotEmpty(activeTray.RecordedUpdates[0]);
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
            setRefreshInterval: _ => { });

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
            setRefreshInterval: interval => configuredInterval = interval);

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
            setRefreshInterval: interval => configuredInterval = interval);

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
    public void AppWiringContract_OnSettingsChangedBodyAppliesSettings_AndShouldStartHiddenIsPreserved()
    {
        var solutionRoot = FindSolutionRoot();
        var appXamlCsPath = Path.Combine(solutionRoot, "src", "AIMonitor.Presentation.Wpf", "App.xaml.cs");
        Assert.True(File.Exists(appXamlCsPath), $"App.xaml.cs not found at: {appXamlCsPath}");

        var fileContent = File.ReadAllText(appXamlCsPath);

        // 1. Assert file contains TrayPolicy.ShouldStartHidden
        Assert.Contains("TrayPolicy.ShouldStartHidden", fileContent);

        // 2. Extract the BODY of OnSettingsChanged method via brace matching
        const string methodSignature = "OnSettingsChanged(AppSettings settings)";
        var methodIndex = fileContent.IndexOf(methodSignature, StringComparison.Ordinal);
        if (methodIndex < 0)
        {
            methodIndex = fileContent.IndexOf("OnSettingsChanged", StringComparison.Ordinal);
        }
        Assert.True(methodIndex >= 0, "Method OnSettingsChanged was not found in App.xaml.cs");

        var openBraceIndex = fileContent.IndexOf('{', methodIndex);
        Assert.True(openBraceIndex >= 0, "Opening brace for OnSettingsChanged was not found.");

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

        Assert.True(closeBraceIndex > openBraceIndex, "Matching closing brace for OnSettingsChanged was not found.");

        var methodBody = fileContent.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);

        // 3. Assert the body of OnSettingsChanged invokes .Apply(
        Assert.Contains(".Apply(", methodBody);
    }

    [Fact]
    public void LiveSettingsApplier_UsesTrayPolicyShouldShowTray_ContractTest()
    {
        var solutionRoot = FindSolutionRoot();
        var applierPath = Path.Combine(solutionRoot, "src", "AIMonitor.Presentation.Wpf", "LiveSettingsApplier.cs");
        Assert.True(File.Exists(applierPath), $"LiveSettingsApplier.cs not found at: {applierPath}");

        var fileContent = File.ReadAllText(applierPath);

        Assert.Contains("TrayPolicy.ShouldShowTray", fileContent);
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
        public Action? OnDisposing { get; set; }

        public void UpdateReadings(IEnumerable<TrayReading> readings)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(FakeTrayHost), "UpdateReadings called on disposed tray host.");
            }

            RecordedUpdates.Add(readings.ToList());
        }

        public void Dispose()
        {
            OnDisposing?.Invoke();
            DisposeCount++;
            IsDisposed = true;
        }
    }
}
