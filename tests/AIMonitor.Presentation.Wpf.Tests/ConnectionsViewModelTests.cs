using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class ConnectionsViewModelTests
{
    private static (ConnectionsViewModel ViewModel, SettingsSession Session, BlockingSettingsStore Store) CreateViewModel(AppSettings? initial = null)
    {
        var settings = initial ?? new AppSettings();
        var store = new BlockingSettingsStore(settings);
        store.Release(); // unblock saves
        var session = new SettingsSession(store, settings);
        var vm = new ConnectionsViewModel(session);
        return (vm, session, store);
    }

    [Fact]
    public void Constructor_InitializesThreeCardsInCorrectOrder()
    {
        var (vm, _, _) = CreateViewModel();

        Assert.Equal(3, vm.Cards.Count);
        Assert.Equal("claude", vm.Cards[0].Meta.Id);
        Assert.Equal("openai", vm.Cards[1].Meta.Id);
        Assert.Equal("gemini", vm.Cards[2].Meta.Id);
        Assert.Same(vm.ClaudeCard, vm.Cards[0]);
        Assert.Same(vm.OpenAiCard, vm.Cards[1]);
        Assert.Same(vm.GeminiCard, vm.Cards[2]);
    }

    [Fact]
    public void Constructor_InitializesShowAtStartup_FromSession()
    {
        var (vmTrue, _, _) = CreateViewModel(new AppSettings { ShowConnectionsAtStartup = true });
        Assert.True(vmTrue.ShowAtStartup);

        var (vmFalse, _, _) = CreateViewModel(new AppSettings { ShowConnectionsAtStartup = false });
        Assert.False(vmFalse.ShowAtStartup);
    }

    [Fact]
    public void Update_MatchesSnapshotsToCardsByProviderId_WithoutSwapping()
    {
        var (vm, _, _) = CreateViewModel();

        var claudeSnap = new ProviderSnapshot(
            "claude",
            configured: true,
            detection: new DetectionInfo("claude", DetectionState.Connected, "cli", "Claude CLI", "claude-user"));

        var openAiSnap = new ProviderSnapshot(
            "openai",
            configured: true,
            detection: new DetectionInfo("openai", DetectionState.Limited, "admin", "Admin Key", "org-99"));

        var geminiSnap = new ProviderSnapshot(
            "gemini",
            configured: true,
            detection: new DetectionInfo("gemini", DetectionState.NotConnected, "", "", ""));

        var dict = new Dictionary<string, ProviderSnapshot>
        {
            ["openai"] = openAiSnap,
            ["gemini"] = geminiSnap,
            ["claude"] = claudeSnap
        };

        vm.Update(dict);

        Assert.Equal(DetectionState.Connected, vm.ClaudeCard.Detection?.State);
        Assert.Equal("claude-user", vm.ClaudeCard.Account);

        Assert.Equal(DetectionState.Limited, vm.OpenAiCard.Detection?.State);
        Assert.Equal("org-99", vm.OpenAiCard.Account);

        Assert.Equal(DetectionState.NotConnected, vm.GeminiCard.Detection?.State);
        Assert.Equal("Not signed in", vm.GeminiCard.Account);
    }

    [Fact]
    public async Task ShowAtStartup_WhenSet_UpdatesShowConnectionsAtStartupInSession()
    {
        var (vm, session, store) = CreateViewModel(new AppSettings { ShowConnectionsAtStartup = true });

        vm.ShowAtStartup = false;
        if (vm.LastSaveTask is not null)
        {
            await vm.LastSaveTask;
        }

        Assert.False(session.Current.ShowConnectionsAtStartup);
        Assert.NotEmpty(store.SavedSettings);
        Assert.False(store.SavedSettings.Last().ShowConnectionsAtStartup);
    }

    [Fact]
    public async Task ShowAtStartup_PreservesOtherSettingsLikeTheme_WhenModifiedConcurrently()
    {
        var (vm, session, store) = CreateViewModel(new AppSettings
        {
            Theme = "light",
            ShowConnectionsAtStartup = true,
            RefreshIntervalSeconds = 90
        });

        // Concurrently change Theme on session
        await session.UpdateAsync(s => s with { Theme = "dark" });

        // Update ShowAtStartup
        vm.ShowAtStartup = false;
        if (vm.LastSaveTask is not null)
        {
            await vm.LastSaveTask;
        }

        Assert.Equal("dark", session.Current.Theme);
        Assert.False(session.Current.ShowConnectionsAtStartup);
        Assert.Equal(90, session.Current.RefreshIntervalSeconds);

        var lastSaved = store.SavedSettings.Last();
        Assert.Equal("dark", lastSaved.Theme);
        Assert.False(lastSaved.ShowConnectionsAtStartup);
        Assert.Equal(90, lastSaved.RefreshIntervalSeconds);
    }

    [Fact]
    public void Commands_FireExpectedEvents()
    {
        var (vm, _, _) = CreateViewModel();

        string? redetectedTarget = "non-null-sentinel";
        var dashboardRequested = false;

        vm.RedetectRequested += target => redetectedTarget = target;
        vm.OpenDashboardRequested += () => dashboardRequested = true;

        vm.RedetectAllCommand.Execute(null);
        Assert.Null(redetectedTarget);

        vm.OpenDashboardCommand.Execute(null);
        Assert.True(dashboardRequested);
    }

    [Fact]
    public void CardEvents_AreForwardedByConnectionsViewModel()
    {
        var (vm, _, _) = CreateViewModel();

        string? connectedProvider = null;
        string? redetectedProvider = null;

        vm.ConnectRequested += id => connectedProvider = id;
        vm.RedetectRequested += id => redetectedProvider = id;

        vm.OpenAiCard.ConnectCommand.Execute(null);
        Assert.Equal("openai", connectedProvider);

        vm.GeminiCard.RedetectCommand.Execute(null);
        Assert.Equal("gemini", redetectedProvider);
    }
}
