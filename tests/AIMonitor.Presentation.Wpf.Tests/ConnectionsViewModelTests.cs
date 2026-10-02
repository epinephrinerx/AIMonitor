using System.IO;
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

    [Fact]
    public void Update_WhenSnapshotDetectionIsNull_AndCardHadPreviousDetection_KeepsPreviousDetection()
    {
        var (vm, _, _) = CreateViewModel();

        var initialDetection = new DetectionInfo("claude", DetectionState.Connected, "cli", "Claude CLI", "claude-user@example.com");
        var initialSnap = new ProviderSnapshot("claude", configured: true, detection: initialDetection);
        vm.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = initialSnap });

        Assert.Equal(DetectionState.Connected, vm.ClaudeCard.Detection?.State);
        Assert.Equal("Connected", vm.ClaudeCard.StateWord);
        Assert.Equal("claude-user@example.com", vm.ClaudeCard.Account);

        // Snapshot with Detection == null (e.g. unexpected error on refresh)
        var errorSnap = new ProviderSnapshot("claude", configured: false, error: "Network timeout");
        vm.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = errorSnap });

        // Must keep previous detection
        Assert.Equal(DetectionState.Connected, vm.ClaudeCard.Detection?.State);
        Assert.Equal("Connected", vm.ClaudeCard.StateWord);
        Assert.Equal("claude-user@example.com", vm.ClaudeCard.Account);
    }

    [Fact]
    public void Update_WhenSnapshotDetectionIsNull_AndCardNeverHadDetection_ShowsUnavailable()
    {
        var (vm, _, _) = CreateViewModel();

        var errorSnap = new ProviderSnapshot("openai", configured: false, error: "App server crashed");
        vm.Update(new Dictionary<string, ProviderSnapshot> { ["openai"] = errorSnap });

        Assert.Null(vm.OpenAiCard.Detection);
        Assert.Equal("Unavailable", vm.OpenAiCard.StateWord);
        Assert.Equal("!", vm.OpenAiCard.StateGlyph);
        Assert.Equal("Could not check", vm.OpenAiCard.Account);
        Assert.Equal("App server crashed", vm.OpenAiCard.SourceLine);
        Assert.Equal("Connect...", vm.OpenAiCard.ConnectButtonText);
    }

    [Fact]
    public void Update_WhenCardHasNoSnapshotAtAll_StaysChecking()
    {
        var (vm, _, _) = CreateViewModel();

        // Card never had a snapshot and snapshots dictionary is empty
        vm.Update(new Dictionary<string, ProviderSnapshot>());

        Assert.Null(vm.ClaudeCard.Detection);
        Assert.Equal("Checking...", vm.ClaudeCard.StateWord);
        Assert.Equal("–", vm.ClaudeCard.StateGlyph);
        Assert.Equal("Not signed in", vm.ClaudeCard.Account);
        Assert.Equal(ProviderMeta.Claude.Tagline, vm.ClaudeCard.SourceLine);
    }

    [Fact]
    public async Task ShowAtStartup_WhenSaveThrowsIOException_RevertsValue_RaisesSaveFailed_AndObservesException()
    {
        var initial = new AppSettings { ShowConnectionsAtStartup = true };
        var store = new BlockingSettingsStore(initial);
        store.Release();
        store.FailOnSave = new IOException("Disk write failure");
        var session = new SettingsSession(store, initial);
        var vm = new ConnectionsViewModel(session);

        string? failedMessage = null;
        vm.SaveFailed += msg => failedMessage = msg;

        var propertyChangedList = new List<string?>();
        vm.PropertyChanged += (_, e) => propertyChangedList.Add(e.PropertyName);

        // Attempt change
        vm.ShowAtStartup = false;

        // Await observed task
        Assert.NotNull(vm.LastSaveTask);
        await vm.LastSaveTask;

        // Value must be reverted to persisted value (true)
        Assert.True(vm.ShowAtStartup);
        Assert.Contains(nameof(ConnectionsViewModel.ShowAtStartup), propertyChangedList);

        // Generic message, no exception text
        Assert.NotNull(failedMessage);
        Assert.DoesNotContain("Disk write failure", failedMessage);
        Assert.DoesNotContain("IOException", failedMessage);
    }
}
