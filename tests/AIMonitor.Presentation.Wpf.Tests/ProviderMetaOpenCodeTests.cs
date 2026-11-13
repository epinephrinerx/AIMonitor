using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 F6 (Docs/SPEC_R020_R021.md): <c>ProviderMeta.OpenCode</c> verbatim, NOT in <c>All</c> (so the
/// Settings provider toggles stay exactly Claude, OpenAI, AGY), and <c>TryGet("opencode")</c> returns it.
/// F10: the existing dialog opens for it in its no-key form ("OpenCode sign-in", SetupHint).
/// </summary>
public sealed class ProviderMetaOpenCodeTests
{
    // F6, verbatim from the spec table (em dashes U+2014, straight apostrophes, \n\n between paragraphs).
    private const string ExpectedHint =
        "Install OpenCode, then sign in from a terminal: run `opencode auth login` and choose a provider (or use /connect inside OpenCode). Run `opencode auth list` to check your sign-ins.\n\n" +
        "This app runs `opencode --version` and `opencode auth list` to show provider names and sign-in types — never keys or tokens — and never signs in, signs out or edits OpenCode's files. On OpenCode 2.x that command talks to OpenCode's background service and starts it if it is not running, as any OpenCode command does; a service started for the first time may import an older auth.json into OpenCode's own database (OpenCode's normal upgrade step, not done by this app). OpenCode usage and cost in the OpenCode tab still come from ccusage.";

    [Fact]
    public void OpenCode_Metadata_MatchesTheSpecVerbatim_F6()
    {
        var meta = ProviderMeta.OpenCode;

        Assert.Equal("opencode", meta.Id);
        Assert.Equal("OpenCode", meta.DisplayName);
        Assert.Equal("O", meta.BadgeLetter);
        Assert.Equal("OpenCode CLI · provider sign-ins", meta.Tagline);
        Assert.False(meta.NeedsKey);
        Assert.Equal("", meta.KeyLabel);
        Assert.Equal("", meta.KeyPlaceholder);
        Assert.Equal("", meta.ExtraLabel);
        Assert.Equal("", meta.ExtraPlaceholder);
        Assert.Equal(ExpectedHint, meta.SetupHint);
    }

    [Fact]
    public void OpenCode_IsNotInAll_AndAllIsUnchanged_F6()
    {
        Assert.DoesNotContain(ProviderMeta.All, m => m.Id == "opencode");
        Assert.Equal(new[] { "claude", "openai", "agy" }, ProviderMeta.All.Select(m => m.Id));
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("OpenCode")]
    [InlineData("OPENCODE")]
    public void TryGet_ReturnsOpenCode_CaseInsensitive_F6(string id)
    {
        Assert.Same(ProviderMeta.OpenCode, ProviderMeta.TryGet(id));
    }

    [Fact]
    public void SettingsProviderToggles_StayClaudeOpenAiAgy_AndNeverListOpenCode_F6()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore(settings);
        using var session = new SettingsSession(store, settings);

        var vm = new SettingsViewModel(session, new FakeStartupRegistrar());

        Assert.Equal(new[] { "claude", "openai", "agy" }, vm.Providers.Select(p => p.Id));
        Assert.DoesNotContain(vm.Providers, p => p.Id == "opencode" || p.DisplayName.Contains("OpenCode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SavingSettings_NeverWritesAnOpenCodeProviderPreference_F6()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore(settings);
        using var session = new SettingsSession(store, settings);
        var vm = new SettingsViewModel(session, new FakeStartupRegistrar());

        await vm.SaveAsync();

        Assert.DoesNotContain(session.Current.Providers.Keys, k => string.Equals(k, "opencode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConnectDialog_ForOpenCode_IsTheNoKeyForm_WithTheSetupHint_F10()
    {
        var secretStore = new TestSecretStore(new Dictionary<string, string>());
        var settings = new AppSettings();
        var settingsStore = new BlockingSettingsStore(settings);
        settingsStore.Release();
        var session = new SettingsSession(settingsStore, settings);
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        var vm = new ConnectDialogViewModel(ProviderMeta.OpenCode, null, new ProviderConnection(null, ""), connectionStore);

        Assert.Equal("OpenCode sign-in", vm.Title);
        Assert.False(vm.NeedsKey);
        Assert.False(vm.CanBrowse);
        Assert.Equal(ExpectedHint, vm.Meta.SetupHint);
    }

    private sealed class FakeSettingsStore(AppSettings initial) : ISettingsStore
    {
        public bool Exists => true;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(initial);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegistered() => false;

        public void Register(string executablePath, string arguments = "")
        {
        }

        public void Unregister()
        {
        }
    }
}
