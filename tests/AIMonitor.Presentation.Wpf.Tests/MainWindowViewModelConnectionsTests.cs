using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class MainWindowViewModelConnectionsTests
{
    private sealed class TestQuotaClient(Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> handler)
        : IProviderQuotaClient
    {
        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    [Fact]
    public void Constructor_InitializesConnectionsViewModel_WithInitialVisibilityFromSettings()
    {
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var storeTrue = new BlockingSettingsStore(new AppSettings { ShowConnectionsAtStartup = true });
        storeTrue.Release();
        using var sessionTrue = new SettingsSession(storeTrue, new AppSettings { ShowConnectionsAtStartup = true });
        var vmTrue = new MainWindowViewModel(coordinator, sessionTrue);

        Assert.NotNull(vmTrue.Connections);
        Assert.True(vmTrue.IsConnectionsPageVisible);

        var storeFalse = new BlockingSettingsStore(new AppSettings { ShowConnectionsAtStartup = false });
        storeFalse.Release();
        using var sessionFalse = new SettingsSession(storeFalse, new AppSettings { ShowConnectionsAtStartup = false });
        var vmFalse = new MainWindowViewModel(coordinator, sessionFalse);

        Assert.NotNull(vmFalse.Connections);
        Assert.False(vmFalse.IsConnectionsPageVisible);
    }

    [Fact]
    public void ShowConnectionsCommand_AndShowDashboardCommand_ToggleConnectionsVisibility()
    {
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings { ShowConnectionsAtStartup = false });
        store.Release();
        using var session = new SettingsSession(store, new AppSettings { ShowConnectionsAtStartup = false });
        var vm = new MainWindowViewModel(coordinator, session);

        Assert.False(vm.IsConnectionsPageVisible);

        vm.ShowConnectionsCommand.Execute(null);
        Assert.True(vm.IsConnectionsPageVisible);

        vm.ShowDashboardCommand.Execute(null);
        Assert.False(vm.IsConnectionsPageVisible);
    }

    [Fact]
    public async Task RefreshAsync_UpdatesConnectionsCardsWithLatestDetections()
    {
        var snapshotClaude = new ProviderSnapshot(
            providerId: "claude",
            configured: true,
            detection: new DetectionInfo("claude", DetectionState.Connected, "claude_code", "Claude Code", "claude-user@example.com"));

        var snapshotOpenAi = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            detection: new DetectionInfo("openai", DetectionState.Limited, "admin_key", "Admin Key", "org-codex"));

        var snapshotGemini = new ProviderSnapshot(
            providerId: "gemini",
            configured: true,
            detection: new DetectionInfo("gemini", DetectionState.NotConnected));

        var clientClaude = new TestQuotaClient((req, ct) => Task.FromResult(snapshotClaude));
        var clientOpenAi = new TestQuotaClient((req, ct) => Task.FromResult(snapshotOpenAi));
        var clientGemini = new TestQuotaClient((req, ct) => Task.FromResult(snapshotGemini));

        var registrations = new[]
        {
            new ProviderClientRegistration("claude", clientClaude),
            new ProviderClientRegistration("openai", clientOpenAi),
            new ProviderClientRegistration("gemini", clientGemini)
        };

        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        // Before refresh, cards are in Checking state
        Assert.Equal("Checking...", vm.Connections.ClaudeCard.StateWord);

        await vm.RefreshAsync();

        // After refresh, each card received its respective detection
        Assert.Equal("Connected", vm.Connections.ClaudeCard.StateWord);
        Assert.Equal("claude-user@example.com", vm.Connections.ClaudeCard.Account);

        Assert.Equal("Limited", vm.Connections.OpenAiCard.StateWord);
        Assert.Equal("org-codex", vm.Connections.OpenAiCard.Account);

        Assert.Equal("Not connected", vm.Connections.GeminiCard.StateWord);
        Assert.Equal("Not signed in", vm.Connections.GeminiCard.Account);
    }

    [Fact]
    public async Task Connections_RedetectRequested_TriggersRefreshAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCount = 0;
        var client = new TestQuotaClient((req, ct) =>
        {
            Interlocked.Increment(ref refreshCount);
            tcs.TrySetResult(true);
            return Task.FromResult(new ProviderSnapshot("claude", configured: true));
        });

        var registrations = new[] { new ProviderClientRegistration("claude", client) };
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        // Trigger RedetectAllCommand on Connections
        vm.Connections.RedetectAllCommand.Execute(null);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshCount >= 1);
    }

    [Fact]
    public void Connections_ConnectRequested_FiresMainWindowRequestConnect()
    {
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        string? requestedProvider = null;
        vm.RequestConnect += id => requestedProvider = id;

        vm.Connections.OpenAiCard.ConnectCommand.Execute(null);
        Assert.Equal("openai", requestedProvider);
    }

    [Fact]
    public void Connections_OpenDashboardRequested_HidesConnectionsPage()
    {
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings { ShowConnectionsAtStartup = true });
        store.Release();
        using var session = new SettingsSession(store, new AppSettings { ShowConnectionsAtStartup = true });
        var vm = new MainWindowViewModel(coordinator, session);

        Assert.True(vm.IsConnectionsPageVisible);

        vm.Connections.OpenDashboardCommand.Execute(null);
        Assert.False(vm.IsConnectionsPageVisible);
    }
}
